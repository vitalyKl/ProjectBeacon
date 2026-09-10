namespace ProjectBeacon.Application.Tests;

using Application.Milestones;
using Domain.Entities.Projects;
using Domain.Enums;

public sealed class MilestoneOrphanLintTests
{
    [Fact]
    public void OpenTasksOnClosed_ReturnsNonTerminal_OnlyWhenClosed()
    {
        var projectId = Guid.NewGuid();
        var milestone = Milestone.Create("M", null, projectId, 1);
        var open = TaskItem.Create("open", projectId);
        open.SetMilestone(milestone.Id);
        var done = TaskItem.Create("done", projectId);
        done.SetMilestone(milestone.Id);
        done.SetReviewNotes("ok");
        done.MoveToNextStatus();
        done.MoveToNextStatus();

        Assert.Empty(MilestoneOrphanLint.OpenTasksOnClosed(milestone, [open, done]));

        milestone.Close();
        var orphans = MilestoneOrphanLint.OpenTasksOnClosed(milestone, [open, done]);
        Assert.Single(orphans);
        Assert.Equal(open.Id, orphans[0].Id);
    }
}
