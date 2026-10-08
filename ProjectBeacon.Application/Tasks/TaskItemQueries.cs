namespace ProjectBeacon.Application.Tasks;

using Application.Common;
/// <summary>
/// Fields for get task.
/// </summary>
public record GetTaskRequest(Guid TaskId, Guid? ProjectId = null);
/// <summary>
/// Command for get task.
/// </summary>
public record GetTaskCommand(GetTaskRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Fields for list project tasks.
/// </summary>
public record ListProjectTasksRequest(Guid ProjectId);
/// <summary>
/// Command for list project tasks.
/// </summary>
public record ListProjectTasksCommand(ListProjectTasksRequest Request) : ICommand<Result<IList<TaskItemDto>>>;
/// <summary>
/// Fields for list tasks by status.
/// </summary>
public record ListTasksByStatusRequest(Guid ProjectId, string Status);
/// <summary>
/// Command for list tasks by status.
/// </summary>
public record ListTasksByStatusCommand(ListTasksByStatusRequest Request) : ICommand<Result<IList<TaskItemDto>>>;
