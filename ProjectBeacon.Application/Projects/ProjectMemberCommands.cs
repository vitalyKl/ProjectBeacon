namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
using Domain.Enums;
/// <summary>
/// Project, user, and role to add. Fails when the user is already a member.
/// </summary>
public record AddProjectMemberRequest(Guid ProjectId, Guid UserId, MemberRole Role);
/// <summary>
/// Command for add project member.
/// </summary>
public record AddProjectMemberCommand(AddProjectMemberRequest Request, ActorContext Actor) : ICommand<Result<ProjectMemberDto>>;
/// <summary>
/// Fields for remove project member.
/// </summary>
public record RemoveProjectMemberRequest(Guid ProjectId, Guid UserId);
/// <summary>
/// Command for remove project member.
/// </summary>
public record RemoveProjectMemberCommand(RemoveProjectMemberRequest Request, ActorContext Actor) : ICommand<Result>;
/// <summary>
/// Fields for get project members.
/// </summary>
public record GetProjectMembersRequest(Guid ProjectId);
/// <summary>
/// Command for get project members.
/// </summary>
public record GetProjectMembersCommand(GetProjectMembersRequest Request) : ICommand<Result<IList<ProjectMemberDto>>>;
/// <summary>
/// A membership. Login and email are filled when the user row is loaded.
/// </summary>
public record ProjectMemberDto(
    Guid Id,
    Guid UserId,
    MemberRole Role,
    DateTime JoinedAt,
    string? Login = null,
    string? Email = null);
