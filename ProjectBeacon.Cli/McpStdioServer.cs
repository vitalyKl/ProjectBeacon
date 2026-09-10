namespace ProjectBeacon.Cli;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        while (true)
        {
            var message = await ReadMessageAsync(input);
            if (message is null)
                return 0;

            JsonNode? response = Handle(message, workspace);
            if (response is not null)
                await WriteMessageAsync(output, response);
        }
    }

    private static JsonNode? Handle(JsonNode message, FileWorkspace workspace)
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
            "tools/call" => CallTool(id, message["params"], workspace),
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
            Props(("path", "string", true), ("oldText", "string", true), ("newText", "string", true)))
    ];

    private static JsonObject CallTool(JsonNode id, JsonNode? args, FileWorkspace workspace)
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
                _ => ToolError(id, $"Unknown tool: {name}")
            };
        }
        catch (ArgumentException ex)
        {
            return ToolError(id, ex.Message);
        }
    }

    private static string Arg(JsonObject? arguments, string key)
    {
        var value = arguments?[key]?.GetValue<string>();
        if (value is null)
            throw new ArgumentException($"missing {key}");
        return value;
    }

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
        var length = -1;
        while (true)
        {
            var line = await ReadAsciiLineAsync(stream);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
        }

        if (length < 0)
            return null;

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read));
            if (n == 0)
                return null;
            read += n;
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(buffer));
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (b == '\n')
                break;
            if (b != '\r')
                bytes.Add((byte)b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task WriteMessageAsync(Stream stream, JsonNode message)
    {
        var json = message.ToJsonString(Json);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
