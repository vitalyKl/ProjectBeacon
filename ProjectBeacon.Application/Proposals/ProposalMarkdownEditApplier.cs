using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ProjectBeacon.Application.Proposals;

/// <summary>
/// Validates and applies heading-addressed Markdown edits against the exact base revision
/// that a proposal was created from.
/// </summary>
public static class ProposalMarkdownEditApplier
{
    private const int MaximumEdits = 20;
    private const int MaximumHeadingLength = 200;
    private const int MaximumContentLength = 30_000;

    private static readonly Regex HeadingSpecPattern = new(
        @"^(?<marks>#{1,6})[ 	]+(?<text>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryValidate(JsonElement edits, out string? error)
    {
        error = null;
        if (edits.ValueKind != JsonValueKind.Array)
        {
            error = "'edits' must be an array.";
            return false;
        }

        var count = edits.GetArrayLength();
        if (count is < 1 or > MaximumEdits)
        {
            error = $"'edits' must contain 1-{MaximumEdits} operations.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var edit in edits.EnumerateArray())
        {
            index++;
            if (edit.ValueKind != JsonValueKind.Object)
            {
                error = $"Edit {index} must be an object.";
                return false;
            }

            var names = edit.EnumerateObject().Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (names.Count != 3
                || !names.Contains("heading")
                || !names.Contains("op")
                || !names.Contains("content"))
            {
                error = $"Edit {index} must contain exactly 'heading', 'op' and 'content'.";
                return false;
            }

            var headingElement = edit.GetProperty("heading");
            if (headingElement.ValueKind != JsonValueKind.String
                || !TryParseHeadingSpec(headingElement.GetString(), out var level, out var title))
            {
                error = $"Edit {index} heading must be an exact Markdown heading such as '## Security'.";
                return false;
            }

            if (!seen.Add($"{level}\0{Normalize(title)}"))
            {
                error = $"Edit {index} targets the same heading more than once. Combine the changes into one operation.";
                return false;
            }

            var operation = edit.GetProperty("op");
            if (operation.ValueKind != JsonValueKind.String
                || !IsSupportedOperation(operation.GetString()))
            {
                error = $"Edit {index} op must be replace, insert_after or delete.";
                return false;
            }

            var body = edit.GetProperty("content");
            if (body.ValueKind != JsonValueKind.String)
            {
                error = $"Edit {index} content must be a string. Use an empty string only to clear a section or for delete.";
                return false;
            }

            var content = body.GetString() ?? string.Empty;
            if (content.Length > MaximumContentLength)
            {
                error = $"Edit {index} content must not exceed {MaximumContentLength} characters.";
                return false;
            }

            if (string.Equals(operation.GetString(), "insert_after", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(content))
            {
                error = $"Edit {index} insert_after requires non-empty content.";
                return false;
            }

            if (string.Equals(operation.GetString(), "delete", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(content))
            {
                error = $"Edit {index} delete must use an empty content string.";
                return false;
            }
        }

        return true;
    }

    public static bool TryApply(
        string sourceMarkdown,
        JsonElement edits,
        out string result,
        out string? error)
    {
        result = sourceMarkdown;
        if (!TryValidate(edits, out error))
            return false;

        foreach (var edit in edits.EnumerateArray())
        {
            var headingText = edit.GetProperty("heading").GetString()!;
            TryParseHeadingSpec(headingText, out var level, out var title);
            var operation = edit.GetProperty("op").GetString()!;
            var content = edit.GetProperty("content").GetString() ?? string.Empty;

            var document = Markdown.Parse(result);
            var headings = document.OfType<HeadingBlock>()
                .Where(item => item.Span.Start >= 0 && item.Span.End >= item.Span.Start)
                .Select(item => new ParsedHeading(item, GetHeadingText(item)))
                .ToArray();
            var matching = headings
                .Where(item => item.Block.Level == level
                    && string.Equals(Normalize(item.Text), Normalize(title), StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matching.Length == 0)
            {
                error = BuildHeadingError(
                    $"Heading '{headingText}' was not found.",
                    headings);
                return false;
            }

            if (matching.Length > 1)
            {
                error = BuildHeadingError(
                    $"Heading '{headingText}' is ambiguous ({matching.Length} matches).",
                    headings);
                return false;
            }

            var target = matching[0].Block;
            var nextSection = headings
                .Where(item => item.Block.Span.Start > target.Span.Start
                    && item.Block.Level <= target.Level)
                .OrderBy(item => item.Block.Span.Start)
                .FirstOrDefault();
            var sectionEnd = nextSection?.Block.Span.Start ?? result.Length;
            var lineBreak = result.IndexOf('\n', Math.Min(result.Length, target.Span.End + 1));
            var headingLineEnd = lineBreak < 0 ? result.Length : lineBreak + 1;
            sectionEnd = Math.Max(headingLineEnd, sectionEnd);

            switch (operation.ToLowerInvariant())
            {
                case "replace":
                    result = ReplaceSectionBody(result, headingLineEnd, sectionEnd, content);
                    break;
                case "insert_after":
                    result = InsertAfterSection(result, sectionEnd, content);
                    break;
                case "delete":
                    result = DeleteSection(result, target.Span.Start, sectionEnd);
                    break;
                default:
                    error = $"Unsupported Markdown edit operation '{operation}'.";
                    return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryApplyPayload(
        string sourceMarkdown,
        string payloadJson,
        out string result,
        out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("edits", out var edits))
            {
                result = sourceMarkdown;
                error = "'edits' was not found in the proposal payload.";
                return false;
            }

            return TryApply(sourceMarkdown, edits, out result, out error);
        }
        catch (JsonException)
        {
            result = sourceMarkdown;
            error = "The proposal payload is not valid JSON.";
            return false;
        }
    }

    private static string ReplaceSectionBody(string markdown, int bodyStart, int sectionEnd, string content)
    {
        var newLine = GetNewLine(markdown);
        var replacement = content.Trim('\r', '\n');
        if (replacement.Length > 0)
            replacement += newLine + newLine;
        return markdown[..bodyStart] + replacement + markdown[sectionEnd..];
    }

    private static string InsertAfterSection(string markdown, int sectionEnd, string content)
    {
        var prefix = markdown[..sectionEnd];
        var suffix = markdown[sectionEnd..];
        var newLine = GetNewLine(markdown);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", newLine, StringComparison.Ordinal)
            .Trim('\r', '\n');
        var separator = prefix.EndsWith(newLine + newLine, StringComparison.Ordinal)
            ? string.Empty
            : prefix.EndsWith(newLine, StringComparison.Ordinal)
                ? newLine
                : newLine + newLine;
        var inserted = separator + normalized + newLine + newLine;
        return prefix + inserted + suffix;
    }

    private static string GetNewLine(string markdown) =>
        markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string DeleteSection(string markdown, int sectionStart, int sectionEnd)
    {
        var prefix = markdown[..sectionStart];
        var suffix = markdown[sectionEnd..];
        return prefix + suffix;
    }

    private static bool TryParseHeadingSpec(string? value, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumHeadingLength)
            return false;

        var match = HeadingSpecPattern.Match(value.Trim());
        if (!match.Success)
            return false;

        level = match.Groups["marks"].Length;
        title = match.Groups["text"].Value.Trim();
        while (title.EndsWith('#'))
            title = title[..^1].TrimEnd();
        return title.Length > 0;
    }

    private static bool IsSupportedOperation(string? operation) =>
        string.Equals(operation, "replace", StringComparison.OrdinalIgnoreCase)
        || string.Equals(operation, "insert_after", StringComparison.OrdinalIgnoreCase)
        || string.Equals(operation, "delete", StringComparison.OrdinalIgnoreCase);

    private static string GetHeadingText(HeadingBlock heading)
    {
        var builder = new StringBuilder();
        AppendInlineText(heading.Inline, builder);
        return Normalize(builder.ToString());
    }

    private static void AppendInlineText(ContainerInline? container, StringBuilder builder)
    {
        if (container is null)
            return;

        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content.ToString());
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline nested:
                    AppendInlineText(nested, builder);
                    break;
            }
        }
    }

    private static string Normalize(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string BuildHeadingError(string message, IReadOnlyList<ParsedHeading> headings)
    {
        var available = headings
            .Select(item => $"{new string('#', item.Block.Level)} {item.Text}")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();
        return available.Length == 0
            ? message + " The document contains no addressable headings."
            : message + " Available headings: " + string.Join("; ", available) + ".";
    }

    private sealed record ParsedHeading(HeadingBlock Block, string Text);
}
