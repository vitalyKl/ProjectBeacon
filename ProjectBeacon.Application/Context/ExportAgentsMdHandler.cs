namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ExportAgentsMdHandler
{
    private readonly BeaconDbContext _db;

    public ExportAgentsMdHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<string>> HandleAsync(ExportAgentsMdCommand command, CancellationToken ct = default)
    {
        var sections = await _db.ContextSections
            .Where(s =>
                s.ProjectId == command.Request.ProjectId &&
                (command.Request.RepoId == null || s.RepoId == command.Request.RepoId))
            .OrderBy(s => s.Ordinal)
            .ToListAsync(ct);

        var yamlHeader = "---\nmanaged-by: projectbeacon\nrevision: uncompiled\nscope: project\n---\n\n";

        var content = sections.Select(s => $"## {s.Title}\n\n{s.BodyMarkdown}").ToList();

        var markdown = yamlHeader + string.Join("\n\n", content);
        return Result.Ok(markdown);
    }
}
