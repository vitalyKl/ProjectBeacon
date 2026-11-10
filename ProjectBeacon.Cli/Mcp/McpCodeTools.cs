namespace ProjectBeacon.Cli.Mcp;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Application;
using Application.Agents;
using Application.CodeIndex;
using Application.Mcp;
using Application.Tasks;
using Domain.Common;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static partial class McpStdioServer
{
    private static JsonObject[] LocalTools() =>
    [
        Tool(McpToolNames.ReadFile,
            "Read a bounded page from a file inside the project root. offset is a zero-based character offset (not a line number); limit is the maximum number of characters, default 12288 and maximum 12288. The response says whether more remains and gives the exact nextOffset. For a truncated file, call read_file again with the same path and that nextOffset until END OF FILE.",
            Props(("path", "string", true), ("offset", "integer", false), ("limit", "integer", false))),
        Tool(McpToolNames.WriteFile, "Write a file inside the project root. Rejects path escape.",
            Props(("path", "string", true), ("content", "string", true))),
        Tool(McpToolNames.ApplyPatch, "Replace one exact occurrence of oldText with newText in a file.",
            Props(("path", "string", true), ("oldText", "string", true), ("newText", "string", true))),
        Tool(McpToolNames.GetTree, "List files and directories under the project root or a sub-path. Re-scans the working tree on every call.",
            Props(("path", "string", false), ("maxEntries", "integer", false))),
        Tool(McpToolNames.SearchCode, "Literal substring search over text files in the working tree.",
            Props(("query", "string", true), ("path", "string", false), ("maxMatches", "integer", false))),
        Tool(McpToolNames.GetChangedScope, "List working-tree changed files from git status, optionally filtered by a path prefix.",
            Props(("path", "string", false), ("maxFiles", "integer", false))),
        Tool(McpToolNames.GetSignatures, "Extract symbol signatures (classes, methods, functions) from changed or specified source files.",
            Props(("path", "string", false), ("maxFiles", "integer", false))),
        Tool(McpToolNames.GetCallers, "Find single-hop incoming references (callers) for a symbol at a given file/line.",
            Props(("path", "string", true), ("symbolName", "string", true), ("line", "integer", true))),
        Tool(McpToolNames.HashRange, "SHA-256 of an inclusive 1-based line range of a file, as a bare lowercase hex string. For content-staleness comparison.",
            Props(("path", "string", true), ("startLine", "integer", true), ("endLine", "integer", true))),
        Tool(McpToolNames.HashFile, "SHA-256 of the exact bytes of one file under the project root. Returns only a bare lowercase hex digest; it never returns file content.",
            Props(("path", "string", true))),
        Tool(McpToolNames.HashText, "SHA-256 of the supplied UTF-8 text encoded without a byte-order mark. Use this to calculate the expected hash of proposed text content.",
            Props(("content", "string", true)))
    ];

    private static IReadOnlyList<string> SplitPrefixes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];
        return path
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static string FormatTree(TreeResult tree)
    {
        var suffix = tree.Truncated ? ", truncated" : "";
        var lines = new List<string> { $"# Tree ({tree.Entries.Count} entries{suffix})" };
        foreach (var entry in tree.Entries)
        {
            var depth = entry.Path.Count(c => c == '/');
            var name = Path.GetFileName(entry.Path);
            lines.Add(new string(' ', depth * 2) + (entry.IsDirectory ? name + "/" : name));
        }
        return string.Join("\n", lines);
    }

    private static string FormatSearch(SearchResult search)
    {
        if (search.Matches.Count == 0)
            return $"No matches for '{search.Query}'.";

        var suffix = search.Truncated ? " (truncated)" : "";
        return string.Join("\n", search.Matches.Select(m => $"{m.Path}:{m.Line}: {m.Text}")) + suffix;
    }

    private static string FormatFileList(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "No changed files.";
        return string.Join("\n", files);
    }

    private static string FormatFileReadPage(string path, FileReadPage page)
    {
        var end = page.Offset + page.Content.Length;
        var header = $"# File: {path}\n# Character range: [{page.Offset}, {end}) of {page.TotalCharacters}";
        var continuation = page.HasMore
            ? $"\n\n[TRUNCATED — this is only one page, not the complete file. Continue with read_file(path=\"{path}\", offset={page.NextOffset}, limit={page.Limit}).]"
            : "\n\n[END OF FILE]";
        return header + "\n\n" + page.Content + continuation;
    }

    private static string FormatSignatures(SignatureResult result)
    {
        if (result.Files.Count == 0)
            return "No signatures found.";

        var backendSuffix = string.IsNullOrEmpty(result.Backend) ? "" : $", backend: {result.Backend}";
        var lines = new List<string> { $"# Signatures ({result.Files.Count} files{backendSuffix})" };

        foreach (var file in result.Files)
        {
            if (file.Symbols.Count == 0 && file.Error is null)
                continue;
            lines.Add("");
            lines.Add($"== {file.Path} [{file.Backend}] ==");
            if (file.Error is not null)
            {
                lines.Add($"  error: {file.Error}");
                continue;
            }
            foreach (var symbol in file.Symbols)
                lines.Add($"  {symbol.Kind}: {symbol.Name} (line {symbol.Line}) — {symbol.Signature}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatCallers(CallerResult result)
    {
        if (result.Callers.Count == 0)
            return $"No callers found for '{result.Symbol}'.";

        var backendSuffix = string.IsNullOrEmpty(result.Backend) ? "" : $", backend: {result.Backend}";
        var buildsSuffix = result.SolutionBuilds is bool b ? $", solutionBuilds: {b}" : "";
        var lines = new List<string> { $"# Callers of {result.Symbol} ({result.Callers.Count}{backendSuffix}{buildsSuffix})" };

        foreach (var caller in result.Callers)
        {
            var symbolSuffix = caller.Symbol is null ? "" : $" in {caller.Symbol}";
            lines.Add($"  {caller.Path}:{caller.Line}{symbolSuffix} — {caller.Snippet}");
        }

        return string.Join("\n", lines);
    }

    private static string Arg(JsonObject? arguments, string key)
    {
        var value = arguments?[key]?.GetValue<string>();
        if (value is null)
            throw new ArgumentException($"missing {key}");
        return value;
    }

    private static string? OptArg(JsonObject? arguments, string key)
        => arguments?[key]?.GetValue<string>();

    private static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static int? OptInt(JsonObject? arguments, string key)
    {
        var node = arguments?[key];
        if (node is null)
            return null;
        if (node.GetValueKind() == JsonValueKind.Number)
            return node.GetValue<int>();
        if (node.GetValueKind() == JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var value))
            return value;
        return null;
    }

    private static JsonObject TextResult<T>(JsonNode id, Application.Common.Result<T> result, Func<T, string> format)
        => result.Success
            ? Result(id, Content(format(result.Value!)))
            : ToolError(id, result.Error ?? "error");

    private static JsonObject FileResult(JsonNode id, Application.Common.Result<string> result)
        => result.Success
            ? Result(id, Content(result.Value!))
            : ToolError(id, result.Error ?? "error");

    private static JsonObject BoolResult(JsonNode id, Application.Common.Result result)
        => result.Success
            ? Result(id, Content("ok"))
            : ToolError(id, result.Error ?? "error");
}