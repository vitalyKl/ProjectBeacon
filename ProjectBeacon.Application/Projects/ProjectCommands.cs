namespace ProjectBeacon.Application.Projects;

using Application.Common;

public record CreateProjectRequest(string Name, string? Description, Guid OrgId);

public record CreateProjectCommand(CreateProjectRequest Request) : ICommand<Result<ProjectDto>>;

public record UpdateProjectRequest(Guid ProjectId, string? Name, string? Description);

public record UpdateProjectCommand(UpdateProjectRequest Request) : ICommand<Result<ProjectDto>>;

public record GetProjectRequest(Guid ProjectId);

public record GetProjectCommand(GetProjectRequest Request) : ICommand<Result<ProjectDto>>;

public record ListProjectsRequest(Guid? OrgId);

public record ListProjectsCommand(ListProjectsRequest Request) : ICommand<Result<IList<ProjectDto>>>;

public record ProjectDto(
    Guid Id,
    string Name,
    string? Description,
    Guid OrgId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
