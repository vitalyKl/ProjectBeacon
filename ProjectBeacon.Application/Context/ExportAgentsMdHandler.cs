namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Renders the compiled brief as AGENTS.md markdown.
/// </summary>
public class ExportAgentsMdHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ExportAgentsMdHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<string>> HandleAsync(ExportAgentsMdCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Context, AuthAction.Read,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<string>(permission);

        var sections = await db.ContextSections
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