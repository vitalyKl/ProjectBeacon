namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class PipelineSessionTests
{
    private static PipelineSession NewSession(
        PipelineRole role = PipelineRole.Actor,
        string promptContext = "task description + scope")
    {
        return PipelineSession.Create(
            taskId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            role: role,
            promptContext: promptContext);
    }

    [Fact]
    public void Create_SetsReadyWithPrompt()
    {
        var taskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var session = PipelineSession.Create(taskId, projectId, PipelineRole.Planner, "task + project context");

        Assert.Equal(taskId, session.TaskId);
        Assert.Equal(projectId, session.ProjectId);
        Assert.Equal(PipelineRole.Planner, session.Role);
        Assert.Equal("task + project context", session.PromptContext);
        Assert.Equal(SessionStatus.Ready, session.Status);
        Assert.Null(session.SubtaskId);
        Assert.Null(session.ModelBackendId);
        Assert.Null(session.LaunchSpec);
        Assert.Null(session.ExternalSessionId);
        Assert.Null(session.LaunchedAt);
        Assert.Null(session.ClosedAt);
    }

    [Fact]
    public void Create_WithSubtaskAndBackend_SetsFields()
    {
        var subtaskId = Guid.NewGuid();
        var backendId = Guid.NewGuid();

        var session = PipelineSession.Create(
            taskId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            role: PipelineRole.Actor,
            promptContext: "instructions + scope",
            subtaskId: subtaskId,
            modelBackendId: backendId,
            launchSpec: "llama-server --port ${PORT}");

        Assert.Equal(subtaskId, session.SubtaskId);
        Assert.Equal(backendId, session.ModelBackendId);
        Assert.Equal("llama-server --port ${PORT}", session.LaunchSpec);
    }

    [Fact]
    public void Create_EmptyPrompt_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PipelineSession.Create(Guid.NewGuid(), Guid.NewGuid(), PipelineRole.Actor, ""));
        Assert.Equal("promptContext", ex.ParamName);

        Assert.Throws<ArgumentException>(() =>
            PipelineSession.Create(Guid.NewGuid(), Guid.NewGuid(), PipelineRole.Actor, "   "));
    }

    [Fact]
    public void Launch_FromReady_SetsActiveAndLaunchedAt()
    {
        var session = NewSession();

        session.Launch("ext-123");

        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal("ext-123", session.ExternalSessionId);
        Assert.NotNull(session.LaunchedAt);
        Assert.Null(session.ClosedAt);
    }

    [Fact]
    public void Launch_FromActive_Throws()
    {
        var session = NewSession();
        session.Launch();

        Assert.Throws<InvalidOperationException>(() => session.Launch());
    }

    [Fact]
    public void Close_FromActive_SetsClosedAt()
    {
        var session = NewSession();
        session.Launch();

        session.Close();

        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.NotNull(session.ClosedAt);
    }

    [Fact]
    public void Close_FromReady_SetsClosedAt()
    {
        var session = NewSession();

        session.Close();

        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.NotNull(session.ClosedAt);
        Assert.Null(session.LaunchedAt);
    }

    [Fact]
    public void Close_FromClosed_Throws()
    {
        var session = NewSession();
        session.Close();

        Assert.Throws<InvalidOperationException>(() => session.Close());
    }

    [Fact]
    public void Fail_FromActive_SetsFailed()
    {
        var session = NewSession();
        session.Launch();

        session.Fail();

        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.NotNull(session.ClosedAt);
    }

    [Fact]
    public void Fail_FromReady_SetsFailed()
    {
        var session = NewSession();

        session.Fail();

        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.NotNull(session.ClosedAt);
    }

    [Fact]
    public void Fail_FromClosed_Throws()
    {
        var session = NewSession();
        session.Close();

        Assert.Throws<InvalidOperationException>(() => session.Fail());
    }
}
