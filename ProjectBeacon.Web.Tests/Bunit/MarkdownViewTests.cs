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

        Assert.Contains("beacon-markdown", cut.Markup);
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
        var markdown = "Safe text <script>alert(1)</script><img src=x onerror=alert(1)> [bad](javascript:alert(1)) [good](https://safe.example)";

        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));

        Assert.Contains("Safe text", cut.Markup);
        Assert.DoesNotContain("<script", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("onerror", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
        var markdown = $"same **content** {Guid.NewGuid():N}";
        MarkdownView.ClearCacheForTests(markdown);

        Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));
        Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));

        Assert.Equal(1, MarkdownView.CacheMissCountForTests(markdown));
    }

    [Fact]
    public void MarkdownView_ShiftsHeadingsBelowPageHierarchy()
    {
        var markdown = "# Page title\n\n## Section\n\n### Detail";
        var cut = Bunit.RenderComponent<MarkdownView>(ps => ps.Add(p => p.Text, markdown));

        Assert.Contains("<h4>Page title</h4>", cut.Markup);
        Assert.Contains("<h5>Section</h5>", cut.Markup);
        Assert.Contains("<h6>Detail</h6>", cut.Markup);
    }

    [Fact]
    public void MarkdownEditor_RendersEditTabAndCharacterCounter()
    {
        var cut = Bunit.RenderComponent<MarkdownEditor>(ps => ps
            .Add(p => p.Value, "Initial **markdown**")
            .Add(p => p.MaxLength, 120));

        Assert.Contains("Edit", cut.Markup);
        Assert.Contains("Initial", cut.Markup);
        Assert.Contains("120", cut.Markup);
    }

}
