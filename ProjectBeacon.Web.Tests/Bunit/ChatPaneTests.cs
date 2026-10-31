using ProjectBeacon.Application.Chat;
using ProjectBeacon.Application.Agents;
using Bunit;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Web.Features.Chat;

namespace ProjectBeacon.Web.Tests.Bunit;

/// <summary>
/// ChatPane is the presentation shared by the /chat page and ChatDock. It renders parts,
/// the phase block, and the composer from pushed parameters alone, without host state.
/// </summary>
public sealed class ChatPaneTests : BUnitRenderBase
{
    private static ChatPartDto Part(int order, string role, string body) =>
        new(Guid.NewGuid(), Guid.NewGuid(), role, "text", body, null, order, DateTime.UtcNow);

    [Fact]
    public void RendersPartsPhaseAndComposer()
    {
        var parts = new List<ChatPartDto> { Part(1, "user", "hello pane"), Part(2, "assistant", "world pane") };

        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, parts)
            .Add(p => p.Phase, "Completed")
            .Add(p => p.PhaseLabel, "Completed"));

        Assert.Equal(2, cut.FindAll(".beacon-chat-part").Count);
        Assert.Contains("world pane", cut.Markup);
        Assert.Contains("beacon-chat-compose", cut.Markup);
    }

    [Fact]
    public void SessionHeadRendersOnlyWhenRequested()
    {
        var session = new ChatSessionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Proj", Guid.NewGuid(), "ext-1", "Pane session",
            ChatSessionStatus.Idle, DateTime.UtcNow, null);

        var withHead = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Session, session)
            .Add(p => p.ShowSessionHead, true));
        Assert.Contains("Pane session", withHead.Markup);

        var withoutHead = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Session, session));
        Assert.DoesNotContain("Pane session", withoutHead.Markup);
    }


    [Fact]
    public void ModelSettingsAndModeControlsRenderWithTheComposer()
    {
        var backend = new LocalModelBackendDto(
            Guid.NewGuid(), "qwen3.8-Q3-medium", ModelBackendType.LlamaCpp,
            "llama-server -m qwen.gguf", 131072, 30, [], Guid.NewGuid(), null, false, "");
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.ShowModelSelect, true)
            .Add(p => p.Models, new List<LocalModelBackendDto> { backend })
            .Add(p => p.Model, "beacon-local/qwen3.8-Q3-medium")
            .Add(p => p.SelectedContextLimit, 131072)
            .Add(p => p.ModelStatus, "switching")
            .Add(p => p.ModelStatusLabel, "Switching model: qwen3.8-Q3-medium"));

        Assert.Contains("beacon-chat-model-bar", cut.Markup);
        Assert.Contains("beacon-chat-config-button", cut.Markup);
        Assert.Contains("beacon-chat-mode-toggle", cut.Markup);
        Assert.Contains("qwen3.8-Q3-medium", cut.Markup);
        Assert.Contains("beacon-chat-model-progress", cut.Markup);
        Assert.Contains("Switching model: qwen3.8-Q3-medium", cut.Markup);
        Assert.True(cut.Markup.IndexOf("beacon-chat-model-bar") < cut.Markup.IndexOf("beacon-chat-compose"),
            "model settings must sit above the composer");
    }

    [Fact]
    public void RestoredSessionDisplaysPersistedPlanMode()
    {
        var session = SessionDto("Plan session") with { AgentMode = "plan" };
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Session, session)
            .Add(p => p.ShowModelSelect, true));

        Assert.Equal("Plan", cut.Find(".beacon-chat-mode-toggle").TextContent.Trim());
    }

    [Fact]
    public void ModelBarHiddenWithoutShowModelSelect()
    {
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.ShowSessionHead, true)
            .Add(p => p.Session, SessionDto("A")));
        Assert.DoesNotContain("beacon-chat-model-bar", cut.Markup);
        Assert.DoesNotContain("beacon-chat-model-select", cut.Markup);
    }

    [Fact]
    public async Task Follow_ScrolledAway_ShowsJumpButton()
    {
        Bunit.JSInterop.Setup<bool>("BeaconChat.followLatest", _ => true).SetResult(false);
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, new List<ChatPartDto> { Part(1, "user", "one") }));
        Assert.Empty(cut.FindAll(".beacon-chat-jump"));

        cut.SetParametersAndRender(p => p.Add(x => x.Parts, new List<ChatPartDto>
        {
            Part(1, "user", "one"), Part(2, "assistant", "two")
        }));
        cut.WaitForElement(".beacon-chat-jump", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Follow_NearLatest_KeepsLogPinned()
    {
        Bunit.JSInterop.Setup<bool>("BeaconChat.followLatest", _ => true).SetResult(true);
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, new List<ChatPartDto> { Part(1, "user", "one") }));
        cut.SetParametersAndRender(p => p.Add(x => x.Parts, new List<ChatPartDto>
        {
            Part(1, "user", "one"), Part(2, "assistant", "two")
        }));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Bunit.JSInterop.Invocations.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25, Xunit.TestContext.Current.CancellationToken);
        Assert.True(Bunit.JSInterop.Invocations.Count > 0);
        Assert.Empty(cut.FindAll(".beacon-chat-jump"));
    }

    [Fact]
    public async Task Follow_SlidingWindow_KeepsLogPinned()
    {
        Bunit.JSInterop.Setup<bool>("BeaconChat.followLatest", _ => true).SetResult(true);
        var p1 = Part(1, "user", "one");
        var p2 = Part(2, "assistant", "two");
        var p3 = Part(3, "assistant", "three");
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, new List<ChatPartDto> { p1, p2 }));
        var before = FollowCalls();

        // The dock pushes a sliding window: the part count stays two, the content moves.
        cut.SetParametersAndRender(p => p.Add(x => x.Parts, new List<ChatPartDto> { p2, p3 }));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (FollowCalls() == before && DateTime.UtcNow < deadline)
            await Task.Delay(25, Xunit.TestContext.Current.CancellationToken);
        Assert.True(FollowCalls() > before, "follow was not triggered by a moving window");
        Assert.Empty(cut.FindAll(".beacon-chat-jump"));
    }

    [Fact]
    public async Task Follow_SessionChange_KeepsLogPinned()
    {
        Bunit.JSInterop.Setup<bool>("BeaconChat.followLatest", _ => true).SetResult(true);
        var a1 = Part(1, "user", "one");
        var a2 = Part(2, "assistant", "two");
        var b1 = Part(1, "user", "three");
        var b2 = Part(2, "assistant", "four");
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Session, SessionDto("A"))
            .Add(p => p.Parts, new List<ChatPartDto> { a1, a2 }));
        var before = FollowCalls();

        // Same part count, different session: the log content is entirely new.
        cut.SetParametersAndRender(p => p
            .Add(x => x.Session, SessionDto("B"))
            .Add(x => x.Parts, new List<ChatPartDto> { b1, b2 }));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (FollowCalls() == before && DateTime.UtcNow < deadline)
            await Task.Delay(25, Xunit.TestContext.Current.CancellationToken);
        Assert.True(FollowCalls() > before, "follow was not triggered by a session change");
        Assert.Empty(cut.FindAll(".beacon-chat-jump"));
    }

    private static string QuestionBody(bool answered) => answered
        ? """{"id":"q1","title":"Pick a tool","fields":[{"key":"choice","title":"Pick","type":"string"}],"answer":{"choice":"a"}}"""
        : """{"id":"q1","title":"Pick a tool","fields":[{"key":"choice","title":"Pick","type":"string"}]}""";

    private static ChatPartDto QuestionPart(Guid id, bool answered) =>
        new(id, Guid.NewGuid(), "assistant", "question", QuestionBody(answered), null, 1, DateTime.UtcNow);

    [Fact]
    public void PendingQuestion_TabShownUntilAnswered()
    {
        var id = Guid.NewGuid();
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, new List<ChatPartDto> { QuestionPart(id, false) }));
        var tabs = cut.FindAll(".beacon-chat-question-tabs");
        Assert.Single(tabs);
        Assert.Contains("Pick a tool", tabs.First().InnerHtml);
        // The part is answered in a later refresh: the tab drops off.
        cut.SetParametersAndRender(p => p.Add(x => x.Parts, new List<ChatPartDto> { QuestionPart(id, true) }));
        Assert.Empty(cut.FindAll(".beacon-chat-question-tabs"));
    }

    [Fact]
    public async Task QuestionTab_Click_ScrollsToPart()
    {
        Bunit.JSInterop.SetupVoid("BeaconChat.scrollToPart", (_args) => true);
        var id = Guid.NewGuid();
        var cut = Bunit.RenderComponent<ChatPane>(parameters => parameters
            .Add(p => p.Parts, new List<ChatPartDto> { QuestionPart(id, false) }));
        cut.Find(".beacon-chat-question-tabs .mud-button").Click();
        var inv = Bunit.JSInterop.Invocations.Single(i => i.Identifier == "BeaconChat.scrollToPart");
        Assert.Equal(id.ToString(), (string?)inv.Arguments[1]);
    }

    private static ChatSessionDto SessionDto(string tag) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Race Project", Guid.NewGuid(), "ext-" + tag, "Session " + tag,
            ChatSessionStatus.Idle, DateTime.UtcNow, null);

    private int FollowCalls() =>
        Bunit.JSInterop.Invocations.Count(i => i.Identifier == "BeaconChat.followLatest");
}