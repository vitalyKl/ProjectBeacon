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
        await using var db = _dbFactory.CreateDbContext();
        var createdCount = 0;
        var stagedRevisions = new HashSet<(Guid TargetId, int Revision)>();

        foreach (var file in command.Files)
        {
            var contextSections = ParseMarkdownFile(command.ProjectId, file.FileName, file.Content);

            foreach (var section in contextSections)
            {
                var existing = await db.ContextSections
                    .FirstOrDefaultAsync(s =>
                        s.ProjectId == section.ProjectId &&
                        s.ScopeType == section.ScopeType &&
                        s.SectionId == section.SectionId &&
                        s.RepoId == section.RepoId &&
                        s.Path == section.Path, ct);

                if (existing is null)
                {
                    db.ContextSections.Add(section);
                    var attribution = ContentAttribution.FromActor(command.Actor);
                    _contentWriter.AddVersion(
                        db, section, attribution.Type ?? "System", attribution.Id ?? file.SourcePath);
                    stagedRevisions.Add((section.Id, section.Revision));
                    createdCount++;
                }
                else
                {
                    var contentChanged =
                        !string.Equals(existing.Title, section.Title, StringComparison.Ordinal)
                        || !string.Equals(existing.BodyMarkdown, section.BodyMarkdown, StringComparison.Ordinal);
                    existing.Update(
                        title: section.Title,
                        bodyMarkdown: section.BodyMarkdown,
                        updatedByType: command.Actor?.Type.ToString().ToLowerInvariant() ?? "system",
                        updatedById: command.Actor?.UserId?.ToString("D") ?? file.SourcePath);
                    if (contentChanged)
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
            // A concurrent writer may commit the unique ContentVersions row before
            // PostgreSQL reports the target row's revision concurrency token. EF rolls
            // back the SaveChanges transaction, so inspect persisted history after the
            // failed batch to distinguish that stale-revision race from unrelated errors.
            if (await HasDuplicateRevisionAsync(db, command.ProjectId, stagedRevisions, ct))
            {
                return Result.Failure<int>(
                    "Imported context changed while it was being saved. Refresh and retry.",
                    ErrorKind.Conflict);
            }

            throw;
        }
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

    private static IList<ContextSection> ParseMarkdownFile(Guid projectId, string fileName, string content)
    {
        var sections = new List<ContextSection>();

        var source = fileName switch
        {
            "AGENTS.md" or "agents.md" => ContextSource.ImportedAgentsMd,
            "CLAUDE.md" or "claude.md" => ContextSource.ImportedClaudeMd,
            _ => fileName.Contains(".cursor") ? ContextSource.ImportedCursor :
                 fileName.Contains(".grok") ? ContextSource.ImportedGrok :
                 fileName.Contains("CODEOWNERS") ? ContextSource.ImportedOwnershipMd :
                 ContextSource.Native
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
            sourcePath: fileName);

        sections.Add(section);
        return sections;
    }
}