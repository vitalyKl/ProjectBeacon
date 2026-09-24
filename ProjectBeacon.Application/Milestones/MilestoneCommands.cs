namespace ProjectBeacon.Application.Milestones;

using Application.Common;

public record CreateMilestoneRequest(string Name, string? Description, Guid ProjectId, int Order);

public record CreateMilestoneCommand(CreateMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;

public record UpdateMilestoneRequest(Guid MilestoneId, string? Name, string? Description, int? Order);

public record UpdateMilestoneCommand(UpdateMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;

public record DeleteMilestoneRequest(Guid MilestoneId);

public record DeleteMilestoneCommand(DeleteMilestoneRequest Request) : ICommand<Result>;

public record CloseMilestoneRequest(Guid MilestoneId);

public record CloseMilestoneCommand(CloseMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;

public record ReopenMilestoneRequest(Guid MilestoneId);

public record ReopenMilestoneCommand(ReopenMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;

public record MilestoneDto(
    Guid Id,
    string Name,
    string? Description,
    Guid ProjectId,
    int Order,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? ClosedAt);
