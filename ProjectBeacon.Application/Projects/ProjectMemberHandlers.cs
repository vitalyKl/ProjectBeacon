namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Audit;
using Application.Common;
using Domain.Entities.Projects;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a project member. Fails when the user is already a member or the caller cannot administer.
/// </summary>
public class AddProjectMemberHandler : ICommandHandler<AddProjectMemberCommand, Result<ProjectMemberDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;
    private readonly IAuditWriter? _auditWriter;

    public AddProjectMemberHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService auth,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ProjectMemberDto>> HandleAsync(AddProjectMemberCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var auth = await _auth.CanAddMemberAsync(
            db, command.Actor, command.Request.ProjectId,
            command.Request.UserId, command.Request.Role, ct);
        if (!auth.Success)
            return Result.Failure<ProjectMemberDto>(auth);

        var alreadyMember = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (alreadyMember)
            return Result.Failure<ProjectMemberDto>("User is already a member of this project.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == command.Request.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProjectMemberDto>("Project not found.");

        var member = ProjectMember.Create(command.Request.ProjectId, command.Request.UserId, command.Request.Role);

        db.ProjectMembers.Add(member);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.member.added",
            "ProjectMember",
            member.Id.ToString(),
            project.OrgId,
            project.Id,
            detailsJson: """{"newState":"Member"}"""));
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(member));
    }

    private static ProjectMemberDto MapToDto(ProjectMember member) =>
        new(member.Id, member.UserId, member.Role, member.JoinedAt);
}
/// <summary>
/// Removes a project member when the caller is allowed to. A missing member fails.
/// </summary>
public class RemoveProjectMemberHandler : ICommandHandler<RemoveProjectMemberCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;
    private readonly IAuditWriter? _auditWriter;

    public RemoveProjectMemberHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService auth,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(RemoveProjectMemberCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var auth = await _auth.CanRemoveMemberAsync(
            db, command.Actor, command.Request.ProjectId,
            command.Request.UserId, ct);
        if (!auth.Success)
            return Result.Failure(auth);

        var member = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (member is null)
            return Result.Failure("Project member not found.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == command.Request.ProjectId, ct);
        if (project is null)
            return Result.Failure("Project not found.");

        db.ProjectMembers.Remove(member);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.member.removed",
            "ProjectMember",
            member.Id.ToString(),
            project.OrgId,
            project.Id,
            detailsJson: """{"previousState":"Member"}"""));
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
/// <summary>
/// Lists members of a project.
/// </summary>
public class GetProjectMembersHandler : ICommandHandler<GetProjectMembersCommand, Result<IList<ProjectMemberDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetProjectMembersHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ProjectMemberDto>>> HandleAsync(GetProjectMembersCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var members = await db.ProjectMembers
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderByDescending(m => m.JoinedAt)
            .ToListAsync(ct);
        var userIds = members.Select(m => m.UserId).ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
        IList<ProjectMemberDto> dtos = members.Select(m =>
        {
            users.TryGetValue(m.UserId, out var user);
            return new ProjectMemberDto(m.Id, m.UserId, m.Role, m.JoinedAt, user?.Login, user?.Email);
        }).ToList();

        return Result.Ok(dtos);
    }
}
