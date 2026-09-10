namespace ProjectBeacon.Application.Tasks;

using Application.Common;

public record ClaimTaskRequest(Guid TaskId, Guid AgentId);

public record ClaimTaskCommand(ClaimTaskRequest Request) : ICommand<Result<TaskItemDto>>;
