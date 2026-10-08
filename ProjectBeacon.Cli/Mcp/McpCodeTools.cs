namespace ProjectBeacon.Cli;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Application;
using Application.Agents;
using Application.CodeIndex;
using Application.Mcp;
using Application.Tasks;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Cli.Mcp;

public static partial class McpStdioServer
{
    private static JsonObject[] LocalTools() =>
    [
        Tool("read_file", "Read a file inside the project root.",
            Props(("path", "string", true))),
        Tool("write_file", "Write a file inside the project root. Rejects path escape.",
            Props(("path", "string", true), ("content", "string", true))),
        Tool("apply_patch", "Replace one exact occurrence of oldText with newText in a file.",
            Props(("path", "string", true), ("oldText", "string", true), ("newText", "string", true))),
        Tool("get_tree", "List files and directories under the project root or a sub-path. Re-scans the working tree on every call.",
            Props(("path", "string", false), ("maxEntries", "integer", false))),
        Tool("search_code", "Literal substring search over text files in the working tree.",
            Props(("query", "string", true), ("path", "string", false), ("maxMatches", "integer", false))),
        Tool("get_changed_scope", "List working-tree changed files from git status, optionally filtered by a path prefix.",
            Props(("path", "string", false), ("maxFiles", "integer", false))),
        Tool("get_signatures", "Extract symbol signatures (classes, methods, functions) from changed or specified source files.",
            Props(("path", "string", false), ("maxFiles", "integer", false))),
        Tool("get_callers", "Find single-hop incoming references (callers) for a symbol at a given file/line.",
            Props(("path", "string", true), ("symbolName", "string", true), ("line", "integer", true))),
        Tool("hash_range", "SHA-256 of an inclusive 1-based line range of a file, as a bare lowercase hex string. For content-staleness comparison.",
            Props(("path", "string", true), ("startLine", "integer", true), ("endLine", "integer", true)))
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