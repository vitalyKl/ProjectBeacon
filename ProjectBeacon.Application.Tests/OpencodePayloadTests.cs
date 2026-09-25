namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Domain.Enums;
using System.Text.Json;

public sealed class OpencodePayloadTests
{
    private static LocalModelBackendDto Backend(string name, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, ModelBackendType.LlamaCpp, "cmd", 4096, 300, [], Guid.NewGuid(), null);

    [Fact]
    public void Solo_SetsSingleModelAndProvider()
    {
        var qwen = Backend("qwen");
        var json = OpencodePayload.BuildApply(@"A:\work\app", AgentRunMode.Solo, [qwen], qwen.Id, null, null, null);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(@"A:\work\app", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("beacon-local/qwen", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("http://127.0.0.1:8080/v1",
            doc.RootElement.GetProperty("provider").GetProperty("beacon-local").GetProperty("options").GetProperty("baseURL").GetString());
        Assert.False(doc.RootElement.TryGetProperty("agent", out _));
    }

    [Fact]
    public void RemoteModel_UsesOpenCodeId_AndStaysOutOfLlamaProvider()
    {
        var grok = new LocalModelBackendDto(
            Guid.NewGuid(), "Grok", ModelBackendType.OpenAiCompatible, "", 0, 0, [], Guid.NewGuid(), null,
            false, "", "xai/grok-3");
        var json = OpencodePayload.BuildApply(@"A:\work\app", AgentRunMode.Solo, [grok], grok.Id, null, null, null);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("xai/grok-3", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("provider").GetProperty("beacon-local").GetProperty("models").EnumerateObject().Any());
    }

    [Fact]
    public void Pipeline_SetsPlanBuildReview()
    {
        var planner = Backend("plan-model");
        var actor = Backend("act-model");
        var review = Backend("rev-model");
        var json = OpencodePayload.BuildApply("/repo", AgentRunMode.Pipeline, [planner, actor, review],
            null, planner.Id, actor.Id, review.Id);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("beacon-local/act-model", doc.RootElement.GetProperty("model").GetString());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("beacon-local/act-model", agent.GetProperty("build").GetProperty("model").GetString());
        Assert.Equal("beacon-local/plan-model", agent.GetProperty("plan").GetProperty("model").GetString());
        Assert.Equal("subagent", agent.GetProperty("review").GetProperty("mode").GetString());
        Assert.Equal("beacon-local/rev-model", agent.GetProperty("review").GetProperty("model").GetString());
    }
}
