namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Authorization;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Lists context sections for the project.
/// </summary>
public class ListContextNodesHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListContextNodesHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<IList<ContextSectionDto>>> HandleAsync(ListContextNodesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Context, AuthAction.Read,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<IList<ContextSectionDto>>(permission);

        var sections = await db.ContextSections
            .Where(s => s.ProjectId == command.Request.ProjectId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);

        return Result.Ok(sections.MapToDtos());
    }
}
/// <summary>
/// Loads one context section. Fails when it is missing.
/// </summary>
public class GetContextNodeHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetContextNodeHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<ContextSectionDto>> HandleAsync(GetContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Context, AuthAction.Read,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<ContextSectionDto>(permission);

        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure<ContextSectionDto>("Context node not found.");

        return Result.Ok(section.MapToDto());
    }
}
/// <summary>
/// Deletes one context section.
/// </summary>
public class DeleteContextNodeHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public DeleteContextNodeHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result> HandleAsync(DeleteContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Context, AuthAction.Delete,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure(permission);

        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure("Context node not found.");

        db.ContextSections.Remove(section);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}