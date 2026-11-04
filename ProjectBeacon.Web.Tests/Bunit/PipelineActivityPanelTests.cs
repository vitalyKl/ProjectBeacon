using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class PipelineActivityPanelTests : BUnitRenderBase
{
    [Fact]
    public void ActiveSessionWithRecentSignal_ShowsWorkingIndicator()
    {
        var session = CreateSession(SessionStatus.Active, DateTime.UtcNow,
            """{"state":"running","activity":"Reading repository files"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("mud-progress-circular", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reading repository files", cut.Markup);
    }

    [Fact]
    public void ActiveSessionWithoutRecentSignal_ShowsStaleWarningInsteadOfSpinner()
    {
        var session = CreateSession(SessionStatus.Active, DateTime.UtcNow.AddMinutes(-3),
            """{"state":"running","activity":"Still processing"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("No recent signal", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mud-progress-circular", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveQueuedSession_ShowsQueueStateInsteadOfWorkingIndicator()
    {
        var session = CreateSession(SessionStatus.Active, DateTime.UtcNow,
            """{"state":"running","activity":"Queued for workstation"}""") with { IsQueued = true };

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("Queued", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mud-progress-circular", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WaitingForUserSession_ShowsWaitingState()
    {
        var session = CreateSession(SessionStatus.WaitingForUser, DateTime.UtcNow,
            """{"state":"waiting-for-user","activity":"Input required"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("Waiting for your answer", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedSession_ShowsPersistedFailureReason()
    {
        var session = CreateSession(SessionStatus.Failed, DateTime.UtcNow, "{}") with
        {
            FailureReason = "native agent crashed"
        };

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("native agent crashed", cut.Markup);
    }

    [Fact]
    public void ReadyPlannerSession_DisablesLaunchAndExplainsMissingNativeExecutor()
    {
        var taskId = Guid.NewGuid();
        var session = CreateSession(SessionStatus.Ready, null, "{}") with
        {
            CanLaunch = false,
            LaunchBlockedReason = "PipelineNoNativeWorkstation"
        };
        var pipeline = new PipelineStateDto(
            taskId, "Pipeline test", TaskItemStatus.InProgress, TaskPipelineStage.Planning, null,
            [], [session], [], CanStart: false,
            StartBlockedReason: "PipelineNoNativeWorkstation",
            Preflight: new PipelinePreflightDto(false, false, false, false, false));

        var cut = Bunit.RenderComponent<Features.Tasks.TaskPipelinePanel>(parameters => parameters
            .Add(component => component.Pipeline, pipeline));

        Assert.Contains("No native workstation is assigned", cut.Markup);
        Assert.Contains("disabled", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private static PipelineSessionDto CreateSession(
        SessionStatus status,
        DateTime? lastActivityAt,
        string activityJson)
    {
        var taskId = Guid.NewGuid();
        return new PipelineSessionDto(
            Guid.NewGuid(), taskId, null, PipelineRole.Planner, status,
            Guid.NewGuid(), null, "Planner prompt",
            lastActivityAt, status is SessionStatus.Failed or SessionStatus.Closed ? DateTime.UtcNow : null,
            activityJson, lastActivityAt);
    }
}
