namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
/// <summary>
/// Name, optional description, and the org the project belongs to.
/// </summary>
public record CreateProjectRequest(string Name, string? Description, Guid OrgId);
/// <summary>
/// Command for create project.
/// </summary>
public record CreateProjectCommand(CreateProjectRequest Request, ActorContext Actor) : ICommand<Result<ProjectDto>>;
/// <summary>
/// Project to rename. An empty name fails. Requires project administer.
/// </summary>
public record UpdateProjectRequest(Guid ProjectId, string? Name, string? Description);
/// <summary>
/// Command for update project.
/// </summary>
public record UpdateProjectCommand(UpdateProjectRequest Request, ActorContext Actor) : ICommand<Result<ProjectDto>>;
/// <summary>
/// Project to delete. Requires project administer. The result is true when the delete commits.
/// </summary>
public record DeleteProjectCommand(Guid ProjectId, ActorContext Actor) : ICommand<Result<bool>>;
/// <summary>
/// Fields for get project.
/// </summary>
public record GetProjectRequest(Guid ProjectId);
/// <summary>
/// Command for get project.
/// </summary>
public record GetProjectCommand(GetProjectRequest Request) : ICommand<Result<ProjectDto>>;
/// <summary>
/// Fields for list projects.
/// </summary>
public record ListProjectsRequest(Guid? OrgId);
/// <summary>
/// Command for list projects.
/// </summary>
public record ListProjectsCommand(ListProjectsRequest Request) : ICommand<Result<IList<ProjectDto>>>;
/// <summary>
/// A project's id, name, description, org, and timestamps.
/// </summary>
public record ProjectDto(
    Guid Id,
    string Name,
    string? Description,
    Guid OrgId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
