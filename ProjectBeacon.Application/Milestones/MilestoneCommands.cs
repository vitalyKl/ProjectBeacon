namespace ProjectBeacon.Application.Milestones;

using Application.Common;
/// <summary>
/// Name, optional description, project, and sort order.
/// </summary>
public record CreateMilestoneRequest(string Name, string? Description, Guid ProjectId, int Order);
/// <summary>
/// Command for create milestone.
/// </summary>
public record CreateMilestoneCommand(CreateMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// Fields for update milestone.
/// </summary>
public record UpdateMilestoneRequest(Guid MilestoneId, string? Name, string? Description, int? Order);
/// <summary>
/// Command for update milestone.
/// </summary>
public record UpdateMilestoneCommand(UpdateMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// Fields for delete milestone.
/// </summary>
public record DeleteMilestoneRequest(Guid MilestoneId);
/// <summary>
/// Command for delete milestone.
/// </summary>
public record DeleteMilestoneCommand(DeleteMilestoneRequest Request) : ICommand<Result>;
/// <summary>
/// Fields for close milestone.
/// </summary>
public record CloseMilestoneRequest(Guid MilestoneId);
/// <summary>
/// Command for close milestone.
/// </summary>
public record CloseMilestoneCommand(CloseMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// Fields for reopen milestone.
/// </summary>
public record ReopenMilestoneRequest(Guid MilestoneId);
/// <summary>
/// Command for reopen milestone.
/// </summary>
public record ReopenMilestoneCommand(ReopenMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// A milestone. ClosedAt is set when it is closed.
/// </summary>
public record MilestoneDto(
    Guid Id,
    string Name,
    string? Description,
    Guid ProjectId,
    int Order,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? ClosedAt);
