using ProjectBeacon.Application.Chat;
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
}