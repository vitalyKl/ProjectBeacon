using Bunit;
using ProjectBeacon.Web.Shared;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class MarkdownViewTests : BUnitRenderBase
{
    [Fact]
    public void MarkdownView_RendersTablesTaskListsAutolinksAndEmphasisExtras()
    {
        var markdown = """
            | Area | State |
            | --- | --- |
            | API | ~~old~~ **updated** |

            - [x] completed
            - [ ] pending

            https://docs.example.com/page
            """;

        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));

        Assert.Contains("<table>", cut.Markup);
        Assert.Contains("<del>old</del>", cut.Markup);
        Assert.Contains("type=\"checkbox\"", cut.Markup);
        Assert.Contains("https://docs.example.com/page", cut.Markup);
        Assert.Contains("target=\"_blank\"", cut.Markup);
        Assert.Contains("rel=\"noopener noreferrer\"", cut.Markup);
    }

    [Fact]
    public void MarkdownView_StripsRawHtmlAndDangerousLinkSchemes()
    {
        var markdown = "Safe text <script>alert(1)</script> [bad](javascript:alert(1)) [good](https://safe.example)";

        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));

        Assert.Contains("Safe text", cut.Markup);
        Assert.DoesNotContain("<script", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://safe.example", cut.Markup);
    }

    [Fact]
    public void MarkdownView_InlineModeOmitsParagraphWrappers()
    {
        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps
            .Add(p => p.Text, "short **formatted** text")
            .Add(p => p.Inline, true));

        Assert.Contains("beacon-markdown-inline", cut.Markup);
        Assert.DoesNotContain("<p>", cut.Markup);
        Assert.Contains("<strong>formatted</strong>", cut.Markup);
    }

    [Fact]
    public void MarkdownView_UsesBoundedHeightAndCanExpand()
    {
        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps
            .Add(p => p.Text, "Long content")
            .Add(p => p.MaxHeight, 80));

        Assert.Contains("max-height:80px", cut.Markup);
        cut.Find("button.beacon-markdown-expand").Click();
        Assert.DoesNotContain("max-height:80px", cut.Markup);
    }

    [Fact]
    public void MarkdownView_CachesRepeatedTextByContentHash()
    {
        MarkdownView.ClearCacheForTests();
        var before = MarkdownView.CacheMissCount;

        Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, "same **content**"));
        Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, "same **content**"));

        Assert.Equal(before + 1, MarkdownView.CacheMissCount);
    }
}
