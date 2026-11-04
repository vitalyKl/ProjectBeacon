using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class PipelineActivityPanelTests : BUnitRenderBase
{
    [Fact]
    public void ActiveSessionWithRecentSignal_ShowsWorkingIndicator()
    {
        var session = CreateSession(
            SessionStatus.Active,
            DateTime.UtcNow,
            """{"state":"thinking","activity":"Preparing the implementation"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("mud-progress-circular", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preparing the implementation", cut.Markup);
    }

    [Fact]
    public void ActiveSessionWithoutRecentSignal_ShowsStaleWarningInsteadOfSpinner()
    {
        var session = CreateSession(
            SessionStatus.Active,
            DateTime.UtcNow.AddMinutes(-3),
            """{"state":"thinking","activity":"Still processing"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("No recent signal", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mud-progress-circular", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WaitingForUserSession_ShowsWaitingState()
    {
        var session = CreateSession(
            SessionStatus.WaitingForUser,
            DateTime.UtcNow,
            """{"state":"waiting-for-user"}""");

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("Waiting for user", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedSession_ShowsPersistedFailureReason()
    {
        var session = CreateSession(
            SessionStatus.Failed,
            DateTime.UtcNow,
            "{}") with { FailureReason = "native agent crashed" };

        var cut = Bunit.RenderComponent<Features.Tasks.PipelineActivityPanel>(parameters => parameters
            .Add(component => component.TaskId, session.TaskId)
            .Add(component => component.Session, session));

        Assert.Contains("native agent crashed", cut.Markup);
    }

    private static PipelineSessionDto CreateSession(
        SessionStatus status,
        DateTime lastActivityAt,
        string activityJson)
    {
        var taskId = Guid.NewGuid();
        return new PipelineSessionDto(
            Guid.NewGuid(),
            taskId,
            null,
            PipelineRole.Planner,
            status,
            Guid.NewGuid(),
            null,
            "Planner prompt",
            DateTime.UtcNow.AddMinutes(-1),
            status is SessionStatus.Failed or SessionStatus.Closed ? DateTime.UtcNow : null,
            activityJson,
            lastActivityAt);
    }
}
