namespace ProjectBeacon.Application.Tasks;

using Application.Common;

public record GetTaskRequest(Guid TaskId, Guid? ProjectId = null);

public record GetTaskCommand(GetTaskRequest Request) : ICommand<Result<TaskItemDto>>;

public record ListProjectTasksRequest(Guid ProjectId);

public record ListProjectTasksCommand(ListProjectTasksRequest Request) : ICommand<Result<IList<TaskItemDto>>>;

public record ListTasksByStatusRequest(Guid ProjectId, string Status);

public record ListTasksByStatusCommand(ListTasksByStatusRequest Request) : ICommand<Result<IList<TaskItemDto>>>;
