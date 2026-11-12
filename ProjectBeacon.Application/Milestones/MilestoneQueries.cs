namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Application.Authorization;
/// <summary>
/// Fields for get milestone.
/// </summary>
public record GetMilestoneRequest(Guid MilestoneId);
/// <summary>
/// Command for get milestone.
/// </summary>
public record GetMilestoneCommand(GetMilestoneRequest Request, ActorContext Actor) : ICommand<Result<MilestoneDto>>;
/// <summary>
/// Fields for list project milestones.
/// </summary>
public record ListProjectMilestonesRequest(Guid ProjectId);
/// <summary>
/// Command for list project milestones.
/// </summary>
public record ListProjectMilestonesCommand(ListProjectMilestonesRequest Request, ActorContext Actor) : ICommand<Result<IList<MilestoneDto>>>;
