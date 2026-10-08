namespace ProjectBeacon.Application.Tasks;

using Application.Common;
/// <summary>
/// Task to move from Todo to InProgress. A task that is not Todo fails as already claimed.
/// </summary>
public record ClaimTaskRequest(Guid TaskId, Guid AgentId, Guid ProjectId);
/// <summary>
/// Command for claim task.
/// </summary>
public record ClaimTaskCommand(ClaimTaskRequest Request) : ICommand<Result<TaskItemDto>>;
