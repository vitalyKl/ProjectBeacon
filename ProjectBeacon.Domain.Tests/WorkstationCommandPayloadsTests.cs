namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;
using Xunit;

public sealed class WorkstationCommandPayloadsTests
{
    [Fact]
    public void PipelineSession_ParsesAllScopeFields()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();
        var json = $$"""{"projectId":"{{projectId}}","taskId":"{{taskId}}","sessionId":"{{sessionId}}","role":"actor","subtaskId":"{{subtaskId}}"}""";

        var parsed = WorkstationCommandPayloads.TryReadPipelineSession(json, out var payload);

        Assert.True(parsed);
        Assert.Equal(projectId, payload.ProjectId);
        Assert.Equal(taskId, payload.TaskId);
        Assert.Equal(sessionId, payload.SessionId);
        Assert.Equal(PipelineRole.Actor, payload.Role);
        Assert.Equal(subtaskId, payload.SubtaskId);
    }

    [Fact]
    public void PipelineSession_AllowsMissingSubtaskForSupervisorRoles()
    {
        var json = $$"""{"projectId":"{{Guid.NewGuid()}}","taskId":"{{Guid.NewGuid()}}","sessionId":"{{Guid.NewGuid()}}","role":"Planner"}""";

        var parsed = WorkstationCommandPayloads.TryReadPipelineSession(json, out var payload);

        Assert.True(parsed);
        Assert.Equal(PipelineRole.Planner, payload.Role);
        Assert.Null(payload.SubtaskId);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void PipelineSession_RejectsUnknownRole(string role)
    {
        var json = $$"""{"projectId":"{{Guid.NewGuid()}}","taskId":"{{Guid.NewGuid()}}","sessionId":"{{Guid.NewGuid()}}","role":"{{role}}"}""";

        Assert.False(WorkstationCommandPayloads.TryReadPipelineSession(json, out _));
    }

    [Fact]
    public void EvalTurn_NormalizesSupportedRuntime()
    {
        var evalRunId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var json = $$"""{"evalRunId":"{{evalRunId}}","taskId":"{{taskId}}","runtime":" Native "}""";

        var parsed = WorkstationCommandPayloads.TryReadEvalTurn(json, out var payload);

        Assert.True(parsed);
        Assert.Equal(evalRunId, payload.EvalRunId);
        Assert.Equal(taskId, payload.TaskId);
        Assert.Equal("native", payload.Runtime);
    }

    [Fact]
    public void EvalTurn_RejectsUnsupportedRuntime()
    {
        var json = $$"""{"evalRunId":"{{Guid.NewGuid()}}","taskId":"{{Guid.NewGuid()}}","runtime":"shell"}""";

        Assert.False(WorkstationCommandPayloads.TryReadEvalTurn(json, out _));
    }

    [Fact]
    public void ChatPrompt_RequiresExternalSessionId()
    {
        var sessionId = Guid.NewGuid();
        var json = $"""{"chatSessionId":"{{sessionId}}","externalSessionId":"ext-123"}""";

        var parsed = WorkstationCommandPayloads.TryReadChatPrompt(json, out var payload);

        Assert.True(parsed);
        Assert.Equal(sessionId, payload.ChatSessionId);
        Assert.Equal("ext-123", payload.ExternalSessionId);
        Assert.False(WorkstationCommandPayloads.TryReadChatPrompt(
            $"""{"chatSessionId":"{{sessionId}}","externalSessionId":" "}""", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("null")]
    public void Readers_RejectMalformedOrNonObjectPayloads(string json)
    {
        Assert.False(WorkstationCommandPayloads.TryReadPipelineSession(json, out _));
        Assert.False(WorkstationCommandPayloads.TryReadEvalTurn(json, out _));
        Assert.False(WorkstationCommandPayloads.TryReadChatPrompt(json, out _));
    }
}
