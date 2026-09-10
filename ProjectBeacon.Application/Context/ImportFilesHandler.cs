namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ImportFilesHandler
{
    private readonly BeaconDbContext _db;

    public ImportFilesHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<int>> HandleAsync(ImportFilesCommand command, CancellationToken ct = default)
    {
        var createdCount = 0;

        foreach (var file in command.Files)
        {
            var contextSections = ParseMarkdownFile(command.ProjectId, file.FileName, file.Content);

            foreach (var section in contextSections)
            {
                var existing = await _db.ContextSections
                    .FirstOrDefaultAsync(s =>
                        s.ProjectId == section.ProjectId &&
                        s.ScopeType == section.ScopeType &&
                        s.SectionId == section.SectionId &&
                        s.RepoId == section.RepoId &&
                        s.Path == section.Path, ct);

                if (existing is null)
                {
                    _db.ContextSections.Add(section);
                    createdCount++;
                }
                else
                {
                    existing.Update(
                        title: section.Title,
                        bodyMarkdown: section.BodyMarkdown,
                        updatedByType: "system",
                        updatedById: file.SourcePath);
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return Result.Ok(createdCount);
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
