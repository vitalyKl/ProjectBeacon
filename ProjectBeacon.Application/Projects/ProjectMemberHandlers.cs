namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class AddProjectMemberHandler : ICommandHandler<AddProjectMemberCommand, Result<ProjectMemberDto>>
{
    private readonly BeaconDbContext _db;

    public AddProjectMemberHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ProjectMemberDto>> HandleAsync(AddProjectMemberCommand command, CancellationToken ct = default)
    {
        var alreadyMember = await _db.ProjectMembers
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (alreadyMember)
            return Result.Failure<ProjectMemberDto>("User is already a member of this project.");

        var member = ProjectMember.Create(command.Request.ProjectId, command.Request.UserId, command.Request.Role);

        _db.ProjectMembers.Add(member);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(member));
    }

    private static ProjectMemberDto MapToDto(ProjectMember member) =>
        new(member.Id, member.UserId, member.Role, member.JoinedAt);
}

public class RemoveProjectMemberHandler : ICommandHandler<RemoveProjectMemberCommand, Result<bool>>
{
    private readonly BeaconDbContext _db;

    public RemoveProjectMemberHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<bool>> HandleAsync(RemoveProjectMemberCommand command, CancellationToken ct = default)
    {
        var member = await _db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (member is null)
            return Result.Failure<bool>("Project member not found.");

        _db.ProjectMembers.Remove(member);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}

public class GetProjectMembersHandler : ICommandHandler<GetProjectMembersCommand, Result<IList<ProjectMemberDto>>>
{
    private readonly BeaconDbContext _db;

    public GetProjectMembersHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<ProjectMemberDto>>> HandleAsync(GetProjectMembersCommand command, CancellationToken ct = default)
    {
        var members = await _db.ProjectMembers
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderByDescending(m => m.JoinedAt)
            .Select(m => new ProjectMemberDto(m.Id, m.UserId, m.Role, m.JoinedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<ProjectMemberDto>)members);
    }
}
