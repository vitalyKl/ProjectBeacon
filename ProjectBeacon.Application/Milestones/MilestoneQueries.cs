namespace ProjectBeacon.Application.Milestones;

using Application.Common;
/// <summary>
/// Fields for get milestone.
/// </summary>
public record GetMilestoneRequest(Guid MilestoneId);
/// <summary>
/// Command for get milestone.
/// </summary>
public record GetMilestoneCommand(GetMilestoneRequest Request) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// Fields for list project milestones.
/// </summary>
public record ListProjectMilestonesRequest(Guid ProjectId);
/// <summary>
/// Command for list project milestones.
/// </summary>
public record ListProjectMilestonesCommand(ListProjectMilestonesRequest Request) : ICommand<Result<IList<MilestoneDto>>>;
