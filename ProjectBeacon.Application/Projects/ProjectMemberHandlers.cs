namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class AddProjectMemberHandler : ICommandHandler<AddProjectMemberCommand, Result<ProjectMemberDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AddProjectMemberHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectMemberDto>> HandleAsync(AddProjectMemberCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var alreadyMember = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (alreadyMember)
            return Result.Failure<ProjectMemberDto>("User is already a member of this project.");

        var member = ProjectMember.Create(command.Request.ProjectId, command.Request.UserId, command.Request.Role);

        db.ProjectMembers.Add(member);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(member));
    }

    private static ProjectMemberDto MapToDto(ProjectMember member) =>
        new(member.Id, member.UserId, member.Role, member.JoinedAt);
}

public class RemoveProjectMemberHandler : ICommandHandler<RemoveProjectMemberCommand, Result<bool>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RemoveProjectMemberHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(RemoveProjectMemberCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (member is null)
            return Result.Failure<bool>("Project member not found.");

        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}

public class GetProjectMembersHandler : ICommandHandler<GetProjectMembersCommand, Result<IList<ProjectMemberDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetProjectMembersHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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