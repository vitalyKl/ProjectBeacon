namespace ProjectBeacon.Cli;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.CodeIndex;
using Application.Mcp;

public static class McpStdioServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task<int> RunAsync(string root)
        => RunAsync(root, Console.OpenStandardInput(), Console.OpenStandardOutput());

    public static async Task<int> RunAsync(string root, Stream input, Stream output)
    {
        var workspace = new FileWorkspace(root);
        var index = new CodeIndex(root);

        while (true)
        {
            var message = await ReadMessageAsync(input);
            if (message is null)
                return 0;

            JsonNode? response = Handle(message, workspace, index);
            if (response is not null)
                await WriteMessageAsync(output, response);
        }
    }

    private static JsonNode? Handle(JsonNode message, FileWorkspace workspace, CodeIndex index)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];

        if (method is null)
            return id is null ? null : Error(id, -32600, "Invalid Request");

        if (method == "notifications/initialized" || method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        if (id is null)
            return null;

        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "beacon", ["version"] = "1.0.0" }
            }),
            "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
            "tools/call" => CallTool(id, message["params"], workspace, index),
            "ping" => Result(id, new JsonObject()),
            _ => Error(id, -32601, $"Unknown method: {method}")
        };
    }

    private static JsonArray Tools() =>
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
            Props(("path", "string", false), ("maxFiles", "integer", false)))
    ];

    private static JsonObject CallTool(JsonNode id, JsonNode? args, FileWorkspace workspace, CodeIndex index)
    {
        var name = args?["name"]?.GetValue<string>();
        JsonObject? arguments = args?["arguments"] as JsonObject;
        if (arguments is null && args?["arguments"] is JsonValue raw && raw.GetValueKind() == JsonValueKind.String)
            arguments = JsonNode.Parse(raw.GetValue<string>() ?? "{}") as JsonObject;

        if (string.IsNullOrEmpty(name))
            return ToolError(id, "malformed path");

        try
        {
            return name switch
            {
                "read_file" => FileResult(id, workspace.ReadFile(Arg(arguments, "path"))),
                "write_file" => BoolResult(id, workspace.WriteFile(Arg(arguments, "path"), Arg(arguments, "content"))),
                "apply_patch" => BoolResult(id, workspace.ApplyPatch(
                    Arg(arguments, "path"),
                    Arg(arguments, "oldText"),
                    Arg(arguments, "newText"))),
                "get_tree" => TextResult(id, index.GetTree(
                    OptArg(arguments, "path"),
                    OptInt(arguments, "maxEntries") ?? CodeIndex.DefaultMaxEntries), FormatTree),
                "search_code" => TextResult(id, index.Search(
                    Arg(arguments, "query"),
                    SplitPrefixes(OptArg(arguments, "path")),
                    OptInt(arguments, "maxMatches") ?? CodeIndex.DefaultMaxMatches), FormatSearch),
                "get_changed_scope" => TextResult(id, index.GetChangedScope(
                    SplitPrefixes(OptArg(arguments, "path")),
                    OptInt(arguments, "maxFiles") ?? CodeIndex.DefaultMaxFiles), FormatFileList),
                _ => ToolError(id, $"Unknown tool: {name}")
            };
        }
        catch (ArgumentException ex)
        {
            return ToolError(id, ex.Message);
        }
    }

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

    private static JsonObject BoolResult(JsonNode id, Application.Common.Result<bool> result)
        => result.Success
            ? Result(id, Content("ok"))
            : ToolError(id, result.Error ?? "error");

    private static JsonObject Content(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text }
        }
    };

    private static JsonObject ToolError(JsonNode id, string message) =>
        Result(id, new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = message }
            }
        });

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static JsonObject Props(params (string Name, string Type, bool Required)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject { ["type"] = field.Type };
            if (field.Required)
                required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static async Task<JsonNode?> ReadMessageAsync(Stream stream)
    {
        while (true)
        {
            var line = await ReadUtf8LineAsync(stream);
            if (line is null)
                return null;
            if (line.Length == 0)
                continue;
            return JsonNode.Parse(line);
        }
    }

    private static async Task<string?> ReadUtf8LineAsync(Stream stream)
    {
        var bytes = new List<byte>(256);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (b == '\n')
                break;
            if (b != '\r')
                bytes.Add((byte)b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task WriteMessageAsync(Stream stream, JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(Json) + "\n");
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
