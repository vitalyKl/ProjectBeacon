namespace ProjectBeacon.Application.Milestones;

using Application.Common;

public record GetMilestoneRequest(Guid MilestoneId);

public record GetMilestoneCommand(GetMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;

public record ListProjectMilestonesRequest(Guid ProjectId);

public record ListProjectMilestonesCommand(ListProjectMilestonesRequest Request) : ICommand<Result<IList<MilestoneDto>>>;
