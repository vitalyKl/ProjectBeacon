using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class PipelineActivityPanelRenderTests : BUnitRenderBase
{
    [Fact]
    public void PipelineActivityPanel_ShowsWorkingSignalOnlyWhenRecentActivityExists()
    {
        var taskId = Guid.NewGuid();
        var session = Session(taskId, SessionStatus.Active,
            """{"state":"running","activity":"Reading repository files"}""", DateTime.UtcNow);

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(builder => builder
            .Add(component => component.TaskId, taskId)
            .Add(component => component.Session, session));

        Assert.Contains("Reading repository files", cut.Markup);
        Assert.Contains("mud-progress-circular", cut.Markup);
        Assert.DoesNotContain("No recent signal", cut.Markup);
    }

    [Fact]
    public void PipelineActivityPanel_ReportsStaleSignalInsteadOfShowingWorkingSpinner()
    {
        var taskId = Guid.NewGuid();
        var session = Session(taskId, SessionStatus.Active,
            """{"state":"running","activity":"Reading repository files"}""", DateTime.UtcNow.AddMinutes(-3));

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(builder => builder
            .Add(component => component.TaskId, taskId)
            .Add(component => component.Session, session));

        Assert.Contains("No recent signal", cut.Markup);
        Assert.DoesNotContain("mud-progress-circular", cut.Markup);
    }

    [Fact]
    public void PipelineActivityPanel_ExposesWaitingForUserAndFailureReason()
    {
        var taskId = Guid.NewGuid();
        var waiting = Session(taskId, SessionStatus.WaitingForUser,
            """{"state":"waiting-for-user","activity":"Input required"}""", DateTime.UtcNow);
        var waitingCut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(builder => builder
            .Add(component => component.TaskId, taskId)
            .Add(component => component.Session, waiting));
        Assert.Contains("Waiting for your answer", waitingCut.Markup);

        var failed = Session(taskId, SessionStatus.Failed,
            """{"state":"failed","activity":"Agent stopped"}""", DateTime.UtcNow, "native agent crashed");
        var failedCut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(builder => builder
            .Add(component => component.TaskId, taskId)
            .Add(component => component.Session, failed));
        Assert.Contains("native agent crashed", failedCut.Markup);
    }

    [Fact]
    public void TaskPipelinePanel_DisablesLaunchAndExplainsMissingNativeExecutor()
    {
        var taskId = Guid.NewGuid();
        var session = Session(taskId, SessionStatus.Ready, "{}", null) with
        {
            CanLaunch = false,
            LaunchBlockedReason = "PipelineNoNativeWorkstation"
        };
        var pipeline = new PipelineStateDto(
            taskId, "Pipeline test", TaskItemStatus.InProgress, TaskPipelineStage.Planning, null,
            [], [session], [], CanStart: false,
            StartBlockedReason: "PipelineNoNativeWorkstation",
            Preflight: new PipelinePreflightDto(false, false, false, false, false));

        var cut = Bunit.RenderComponent<Features.Tasks.TaskPipelinePanel>(builder => builder
            .Add(component => component.Pipeline, pipeline));

        Assert.Contains("No native workstation is assigned", cut.Markup);
        Assert.Contains("disabled", cut.Markup);
    }

    private static PipelineSessionDto Session(
        Guid taskId, SessionStatus status, string activityJson, DateTime? lastActivityAt, string? failureReason = null) =>
        new(Guid.NewGuid(), taskId, null, PipelineRole.Planner, status, null, null, "Planner prompt",
            lastActivityAt, null, activityJson, lastActivityAt, failureReason);
}
