using ProjectBeacon.Application.Chat;
using ProjectBeacon.Web.Features.Chat;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class ChatRenderTests : BUnitRenderBase
{
    private static ChatPartDto Part(string role, string kind, string body,
        string? tool = null, string? toolStatus = null, string? toolInput = null,
        string? toolResult = null, int? durationMs = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), role, kind, body, null, 1,
            new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc),
            tool, toolStatus, toolInput, toolResult, durationMs);

    [Fact]
    public void StructuredToolPart_RendersToolPanel()
    {
        var cut = Bunit.RenderComponent<ChatPartView>(ps => ps.Add(p => p.Part,
            Part("assistant", "tool", "", tool: "search", toolStatus: "completed",
                toolInput: "{\"query\":1}", toolResult: "found 3", durationMs: 1200)));
        var html = cut.Markup;
        Assert.Contains("beacon-tool-panel", html);
        Assert.Contains("search", html);
        Assert.Contains("completed", html);
        Assert.Contains("1200 ms", html);
        Assert.Contains("query", html);
        Assert.Contains("found 3", html);
    }

    [Fact]
    public void LegacyToolPart_RendersExpansionPanel()
    {
        var cut = Bunit.RenderComponent<ChatPartView>(ps => ps.Add(p => p.Part,
            Part("assistant", "tool", "search\nrunning\ndetail body")));
        var html = cut.Markup;
        Assert.DoesNotContain("beacon-tool-panel", html);
        Assert.Contains("search", html);
        Assert.Contains("running", html);
    }

    [Fact]
    public void AssistantText_RendersMarkdownWithCodeToolbar()
    {
        var cut = Bunit.RenderComponent<ChatPartView>(ps => ps.Add(p => p.Part,
            Part("assistant", "text", "hello **world**\n```csharp\nvar x = 1;\n```")));
        var html = cut.Markup;
        Assert.Contains("beacon-markdown", html);
        Assert.Contains("<strong>world</strong>", html);
        Assert.Contains("beacon-code-bar", html);
        Assert.Contains("data-beacon-code-copy", html);
        Assert.Contains("csharp", html);
    }

    [Fact]
    public void UserText_StaysPlainAndEscaped()
    {
        var cut = Bunit.RenderComponent<ChatPartView>(ps => ps.Add(p => p.Part,
            Part("user", "text", "hi <script>alert(1)</script>")));
        var html = cut.Markup;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("beacon-chat-user", html);
        Assert.DoesNotContain("beacon-markdown", html);
    }

    [Fact]
    public void ChatMarkdown_StripsScriptTags()
    {
        var cut = Bunit.RenderComponent<ChatMarkdown>(ps => ps.Add(p => p.Text, "hi <script>alert(1)</script>"));
        Assert.DoesNotContain("<script", cut.Markup);
        Assert.Contains("hi", cut.Markup);
    }

    [Fact]
    public void ChatMarkdown_DropsDangerousSchemes()
    {
        var cut = Bunit.RenderComponent<ChatMarkdown>(ps => ps.Add(p => p.Text,
            "[click](javascript:alert(1))\n![](javascript:alert(1))"));
        Assert.DoesNotContain("javascript:", cut.Markup);
    }

    [Fact]
    public void ChatMarkdown_KeepsHttpLinks()
    {
        var cut = Bunit.RenderComponent<ChatMarkdown>(ps => ps.Add(p => p.Text,
            "[docs](https://docs.example.com/page)"));
        Assert.Contains("https://docs.example.com/page", cut.Markup);
    }


}
