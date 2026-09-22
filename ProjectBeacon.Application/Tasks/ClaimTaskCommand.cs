namespace ProjectBeacon.Application.Tasks;

using Application.Common;

public record ClaimTaskRequest(Guid TaskId, Guid AgentId, Guid ProjectId);

public record ClaimTaskCommand(ClaimTaskRequest Request) : ICommand<Result<TaskItemDto>>;
