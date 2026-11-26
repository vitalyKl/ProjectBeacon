using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class TaskPipelinePanelTests : BUnitRenderBase
{
    [Fact]
    public void QueuedGitHubMerge_ShowsInformationalMessageWithoutLeakingRequestId()
    {
        var taskId = Guid.NewGuid();
        var mergeRequestId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow;
        var landing = new LandingAttemptDto(
            Guid.NewGuid(),
            taskId,
            LandingAttemptStatus.ExecutePending,
            $"beacon/task/{taskId:N}",
            new string('a', 40),
            new string('b', 40),
            Guid.NewGuid(),
            "main",
            new string('c', 40),
            new string('d', 40),
            null,
            null,
            "GitHubMergeQueued:" + mergeRequestId,
            null,
            null,
            null,
            now,
            now);

        var pipeline = new PipelineStateDto(
            taskId,
            "Merge queue task",
            TaskItemStatus.InProgress,
            TaskPipelineStage.Approved,
            null,
            [],
            [],
            [],
            LatestLandingAttempt: landing);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskPipelinePanel>(
            parameters => parameters.Add(component => component.Pipeline, pipeline));

        Assert.Contains("GitHub accepted the merge request.", cut.Markup);
        Assert.DoesNotContain(mergeRequestId, cut.Markup);
    }
}
