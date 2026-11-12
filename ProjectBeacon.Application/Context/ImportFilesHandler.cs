namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Imports markdown files as context sections and returns how many were created.
/// </summary>
public class ImportFilesHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public ImportFilesHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public ImportFilesHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<int>> HandleAsync(ImportFilesCommand command, CancellationToken ct = default)
    {
        if (command.Files is null)
            return Result.Failure<int>("At least one Markdown file is required.");
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in command.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName) || string.IsNullOrWhiteSpace(file.SourcePath))
                return Result.Failure<int>("Each imported Markdown file must include a file name and source path.");
            if (!sourcePaths.Add(file.SourcePath))
                return Result.Failure<int>("A source path may only be imported once per request.");
        }

        await using var db = _dbFactory.CreateDbContext();
        var createdCount = 0;
        var stagedRevisions = new HashSet<(Guid TargetId, int Revision)>();
        var stagedImportIdentities = new HashSet<string>(StringComparer.Ordinal);
        var updatedByType = command.Actor?.Type.ToString().ToLowerInvariant() ?? "system";

        foreach (var file in command.Files)
        {
            var updatedById = command.Actor?.UserId?.ToString("D") ?? file.SourcePath;
            var contextSections = ParseMarkdownFile(
                command.ProjectId, file.FileName, file.Content, file.SourcePath, updatedByType, updatedById);

            foreach (var section in contextSections)
            {
                // SourcePath identifies the imported file. SectionId alone is not unique:
                // separate directories commonly contain files with the same basename.
                var existing = await db.ContextSections
                    .FirstOrDefaultAsync(s =>
                        s.ProjectId == section.ProjectId &&
                        s.ScopeType == section.ScopeType &&
                        s.RepoId == section.RepoId &&
                        s.TaskId == section.TaskId &&
                        s.Path == section.Path &&
                        s.SourcePath == section.SourcePath &&
                        (s.Source != ContextSource.Native
                            || s.UpdatedByType == null
                            || s.UpdatedByType == "system"
                            || s.UpdatedByType == "human"), ct);

                if (existing is not null && existing.SectionId != section.SectionId)
                    return Result.Failure<int>(
                        "The imported source path is already associated with a different context section.",
                        ErrorKind.Conflict);

                // Older imports stored only FileName in SourcePath. Reuse that row once,
                // then upgrade its identity to the full caller-supplied SourcePath.
                if (existing is null)
                {
                    var legacyMatches = await db.ContextSections
                        .Where(s =>
                            s.ProjectId == section.ProjectId &&
                            s.ScopeType == section.ScopeType &&
                            s.RepoId == section.RepoId &&
                            s.TaskId == section.TaskId &&
                            s.Path == section.Path &&
                            s.SectionId == section.SectionId &&
                            s.SourcePath == file.FileName &&
                            (s.Source == section.Source
                                || (s.Source == ContextSource.Native
                                    && (s.UpdatedByType == null
                                        || s.UpdatedByType == "system"
                                        || s.UpdatedByType == "human"))))
                        .Take(2)
                        .ToListAsync(ct);

                    if (legacyMatches.Count > 1)
                        return Result.Failure<int>(
                            "Multiple legacy context sections match this imported file name. Resolve the ambiguity before importing.",
                            ErrorKind.Conflict);

                    existing = legacyMatches.SingleOrDefault();
                }

                if (existing is null)
                {
                    db.ContextSections.Add(section);
                    var attribution = ContentAttribution.FromActor(command.Actor);
                    _contentWriter.AddVersion(
                        db, section, attribution.Type ?? "System", attribution.Id ?? file.SourcePath);
                    stagedRevisions.Add((section.Id, section.Revision));
                    stagedImportIdentities.Add(section.SourcePath!);
                    createdCount++;
                }
                else
                {
                    var previousRevision = existing.Revision;
                    existing.UpdateImportedFile(
                        section.Title,
                        section.BodyMarkdown,
                        section.Source,
                        section.SourcePath!,
                        updatedByType,
                        updatedById);

                    if (existing.Revision != previousRevision)
                    {
                        var attribution = ContentAttribution.FromActor(command.Actor);
                        _contentWriter.AddVersion(
                            db, existing, attribution.Type ?? "System", attribution.Id ?? file.SourcePath);
                        stagedRevisions.Add((existing.Id, existing.Revision));
                    }
                }
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Ok(createdCount);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<int>(
                "Imported context changed while it was being saved. Refresh and retry.",
                ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            // A competing first import may win the unique source-identity index. The
            // failed SaveChanges transaction is rolled back, so the winner is visible here.
            if (await HasDuplicateImportIdentityAsync(db, command.ProjectId, stagedImportIdentities, ct)
                || await HasDuplicateRevisionAsync(db, command.ProjectId, stagedRevisions, ct))
            {
                return Result.Failure<int>(
                    "Imported context changed while it was being saved. Refresh and retry.",
                    ErrorKind.Conflict);
            }

            throw;
        }
    }

    private static async Task<bool> HasDuplicateImportIdentityAsync(
        IBeaconDb db,
        Guid projectId,
        IReadOnlySet<string> stagedImportIdentities,
        CancellationToken ct)
    {
        if (stagedImportIdentities.Count == 0)
            return false;

        return await db.ContextSections.AnyAsync(section =>
            section.ProjectId == projectId
            && section.ScopeType == ContextScopeType.Project
            && section.RepoId == null
            && section.TaskId == null
            && section.Path == string.Empty
            && section.Source != ContextSource.Native
            && section.SourcePath != null
            && stagedImportIdentities.Contains(section.SourcePath), ct);
    }

    private static async Task<bool> HasDuplicateRevisionAsync(
        IBeaconDb db,
        Guid projectId,
        IReadOnlySet<(Guid TargetId, int Revision)> stagedRevisions,
        CancellationToken ct)
    {
        if (stagedRevisions.Count == 0)
            return false;

        var targetIds = stagedRevisions.Select(item => item.TargetId).Distinct().ToArray();
        var revisions = stagedRevisions.Select(item => item.Revision).Distinct().ToArray();
        var persisted = await db.ContentVersions
            .Where(version => version.ProjectId == projectId
                && version.TargetKind == ContentTargetKind.ContextSection
                && targetIds.Contains(version.TargetId)
                && revisions.Contains(version.Revision))
            .Select(version => new { version.TargetId, version.Revision })
            .ToListAsync(ct);

        return persisted.Any(version => stagedRevisions.Contains((version.TargetId, version.Revision)));
    }

    private static IList<ContextSection> ParseMarkdownFile(
        Guid projectId,
        string fileName,
        string content,
        string sourcePath,
        string updatedByType,
        string updatedById)
    {
        var sections = new List<ContextSection>();

        var source = fileName switch
        {
            "AGENTS.md" or "agents.md" => ContextSource.ImportedAgentsMd,
            "CLAUDE.md" or "claude.md" => ContextSource.ImportedClaudeMd,
            "CONVENTIONS.md" or "conventions.md" => ContextSource.ImportedConventionsMd,
            _ => fileName.Contains(".cursor", StringComparison.OrdinalIgnoreCase) ? ContextSource.ImportedCursor :
                 fileName.Contains(".grok", StringComparison.OrdinalIgnoreCase) ? ContextSource.ImportedGrok :
                 fileName.Contains("CODEOWNERS", StringComparison.OrdinalIgnoreCase) ? ContextSource.ImportedOwnershipMd :
                 ContextSource.ImportedMarkdown
        };

        var title = Path.GetFileNameWithoutExtension(fileName);

        var section = ContextSection.Create(
            sectionId: title.ToLowerInvariant().Replace(" ", "_"),
            title: title,
            bodyMarkdown: content,
            projectId: projectId,
            scopeType: ContextScopeType.Project,
            key: null,
            ordinal: 0,
            source: source,
            sourcePath: sourcePath,
            updatedByType: updatedByType,
            updatedById: updatedById);

        sections.Add(section);
        return sections;
    }
}
