namespace ProjectBeacon.Application.Milestones;

using Domain.Entities.Projects;
using Domain.Enums;

public static class MilestoneOrphanLint
{
    public static IReadOnlyList<TaskItem> OpenTasksOnClosed(
        Milestone milestone,
        IEnumerable<TaskItem> tasks)
    {
        if (milestone.ClosedAt is null)
            return [];

        return tasks
            .Where(t => t.MilestoneId == milestone.Id && t.Status != TaskItemStatus.Done)
            .ToList();
    }

    public static int OpenTaskCount(DateTime? closedAt, Guid milestoneId, IEnumerable<(Guid? MilestoneId, string Status)> tasks)
    {
        if (closedAt is null)
            return 0;
        return tasks.Count(t => t.MilestoneId == milestoneId && t.Status != nameof(TaskItemStatus.Done));
    }
}
