namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Enums;

public record AddProjectMemberRequest(Guid ProjectId, Guid UserId, MemberRole Role);

public record AddProjectMemberCommand(AddProjectMemberRequest Request) : ICommand<Result<ProjectMemberDto>>;

public record RemoveProjectMemberRequest(Guid ProjectId, Guid UserId);

public record RemoveProjectMemberCommand(RemoveProjectMemberRequest Request) : ICommand<Result<bool>>;

public record GetProjectMembersRequest(Guid ProjectId);

public record GetProjectMembersCommand(GetProjectMembersRequest Request) : ICommand<Result<IList<ProjectMemberDto>>>;

public record ProjectMemberDto(
    Guid Id,
    Guid UserId,
    MemberRole Role,
    DateTime JoinedAt);
