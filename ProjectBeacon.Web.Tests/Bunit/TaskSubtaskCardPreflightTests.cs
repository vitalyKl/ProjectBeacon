using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class TaskSubtaskCardPreflightTests : BUnitRenderBase
{
    [Fact]
    public void PendingActor_IsDisabledWhenProjectHasNoOnlineNativeWorkstation()
    {
        var taskId = Guid.NewGuid();
        var subtask = new SubtaskDto(
            Guid.NewGuid(),
            "Collect evidence",
            SubtaskStatus.Pending,
            null,
            null,
            0,
            [],
            [],
            DateTime.UtcNow,
            null);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskSubtaskCard>(parameters => parameters
            .Add(component => component.TaskId, taskId)
            .Add(component => component.Subtask, subtask)
            .Add(component => component.Preflight,
                new PipelinePreflightDto(
                    HasNativeWorkstation: true,
                    HasOnlineNativeWorkstation: false,
                    PlannerModelConfigured: true,
                    ActorModelConfigured: true,
                    ReviewModelConfigured: true)));

        Assert.Contains("disabled", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offline", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingActor_IsDisabledWhenActorModelIsNotConfigured()
    {
        var subtask = new SubtaskDto(
            Guid.NewGuid(),
            "Collect evidence",
            SubtaskStatus.Pending,
            null,
            null,
            0,
            [],
            [],
            DateTime.UtcNow,
            null);

        var cut = Bunit.RenderComponent<Features.Tasks.TaskSubtaskCard>(parameters => parameters
            .Add(component => component.TaskId, Guid.NewGuid())
            .Add(component => component.Subtask, subtask)
            .Add(component => component.Preflight,
                new PipelinePreflightDto(
                    HasNativeWorkstation: true,
                    HasOnlineNativeWorkstation: true,
                    PlannerModelConfigured: true,
                    ActorModelConfigured: false,
                    ReviewModelConfigured: true)));

        Assert.Contains("disabled", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
