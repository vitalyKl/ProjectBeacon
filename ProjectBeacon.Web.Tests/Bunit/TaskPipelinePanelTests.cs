using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;
using MudBlazor;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class TaskPipelinePanelTests : BUnitRenderBase
{
    [Fact]
    public void FailedGitHubMerge_ShowsLocalizedFailureReason()
    {
        var taskId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var landing = new LandingAttemptDto(
            Guid.NewGuid(),
            taskId,
            LandingAttemptStatus.Failed,
            $"beacon/task/{taskId:N}",
            new string('a', 40),
            new string('b', 40),
            Guid.NewGuid(),
            "main",
            new string('c', 40),
            new string('d', 40),
            null,
            null,
            "GitHubAsyncMergeFailed",
            "GitHubAsyncMergeFailed",
            null,
            null,
            now,
            now);
        var pipeline = new PipelineStateDto(
            taskId,
            "Failed merge task",
            TaskItemStatus.InProgress,
            TaskPipelineStage.Approved,
            null,
            [],
            [],
            [],
            LatestLandingAttempt: landing);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskPipelinePanel>(
            parameters => parameters.Add(component => component.Pipeline, pipeline));

        Assert.Contains("GitHub rejected the asynchronous merge request.", cut.Markup);
    }

    [Fact]
    public void PendingGitHubMerge_ShowsProcessingMessageWithoutLeakingRequestId()
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
            "GitHubMergePending:" + mergeRequestId,
            null,
            null,
            null,
            now,
            now);
        var pipeline = new PipelineStateDto(
            taskId,
            "Pending merge task",
            TaskItemStatus.InProgress,
            TaskPipelineStage.Approved,
            null,
            [],
            [],
            [],
            LatestLandingAttempt: landing);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskPipelinePanel>(
            parameters => parameters.Add(component => component.Pipeline, pipeline));

        Assert.Contains("GitHub accepted the asynchronous merge request and is still processing it.", cut.Markup);
        Assert.DoesNotContain(mergeRequestId, cut.Markup);
        Assert.NotNull(cut.Find(".mud-alert"));
    }

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
            "GitHubMergeEnqueued:" + mergeRequestId,
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

        Assert.Contains("GitHub has placed the pull request in the merge queue.", cut.Markup);
        Assert.DoesNotContain(mergeRequestId, cut.Markup);
    }
}
