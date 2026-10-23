namespace ProjectBeacon.Web.Features.Chat;

using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

/// <summary>
/// Wraps fenced code blocks in a toolbar with a collapse and a copy button.
/// The emitted tags and attributes are allow-listed by the chat markdown sanitizer.
/// </summary>
internal sealed class ChatCodeBlockRenderer : CodeBlockRenderer
{
    private readonly string _collapseLabel;
    private readonly string _expandLabel;
    private readonly string _copyLabel;

    public ChatCodeBlockRenderer(string collapseLabel, string expandLabel, string copyLabel)
    {
        _collapseLabel = collapseLabel;
        _expandLabel = expandLabel;
        _copyLabel = copyLabel;
    }

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var lang = obj is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info) ? fenced.Info.Trim() : "";
        renderer.Write("<div class=\"beacon-code\"><div class=\"beacon-code-bar\">");
        renderer.Write("<button type=\"button\" class=\"beacon-code-btn\" data-beacon-code-collapse")
            .Write(" data-beacon-code-collapse-label=\"")
            .WriteEscape(_collapseLabel)
            .Write("\" data-beacon-code-expand=\"")
            .WriteEscape(_expandLabel)
            .Write("\" aria-label=\"")
            .WriteEscape(_collapseLabel)
            .Write("\">&#8211;</button>");
        if (lang.Length > 0)
            renderer.Write("<span class=\"beacon-code-lang\">").WriteEscape(lang).Write("</span>");
        renderer.Write("<button type=\"button\" class=\"beacon-code-btn\" data-beacon-code-copy aria-label=\"")
            .WriteEscape(_copyLabel)
            .Write("\">&#8985;</button>");
        renderer.WriteLine("</div>");
        base.Write(renderer, obj);
        renderer.WriteLine("</div>");
    }
}
