namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static async Task<McpToolText> SendAsync(
        BeaconApiClient api, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
        => Format(await api.SendAsync(method, path, body, ct));

    private static async Task<McpToolText> SendRawAsync(
        BeaconApiClient api, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var (ok, status, raw) = await api.SendAsync(method, path, body, ct);
        if (!ok)
            return Fail(status, raw);
        return new McpToolText(false, raw);
    }

    private static McpToolText Format((bool Ok, int Status, string Body) response)
        => response.Ok ? Ok(response.Body, response.Status) : Fail(response.Status, response.Body);

    private static McpToolText Ok(string body, int status)
    {
        if (status == 204 || string.IsNullOrWhiteSpace(body))
            return new McpToolText(false, "ok");
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return new McpToolText(false, JsonNode.Parse(trimmed)!.ToJsonString(Pretty));
            }
            catch (JsonException)
            {
            }
        }

        return new McpToolText(false, body);
    }

    private static McpToolText Fail(int status, string body)
    {
        if (status == 204)
            return new McpToolText(true, "HTTP 204");
        var trimmed = body.Trim();
        if (trimmed.Length > 2000)
            trimmed = trimmed[..2000];
        try
        {
            var error = JsonNode.Parse(trimmed)?["error"];
            if (error is JsonValue value && value.TryGetValue<string>(out var message) && !string.IsNullOrWhiteSpace(message))
                return new McpToolText(true, $"HTTP {status}: {message}");
        }
        catch (JsonException)
        {
        }

        return new McpToolText(true, string.IsNullOrEmpty(trimmed) ? $"HTTP {status}" : $"HTTP {status}: {trimmed}");
    }

    private static bool TryProject(McpEnvironment env, JsonObject? args, out Guid projectId, out string error)
    {
        var raw = Text(args, "projectId");
        if (string.IsNullOrWhiteSpace(raw))
            raw = env.Get("BEACON_PROJECT_ID");
        if (Guid.TryParse(raw, out projectId))
        {
            error = "";
            return true;
        }

        projectId = Guid.Empty;
        error = "BEACON_PROJECT_ID is not set (or is not a GUID). Set it to the project GUID for this MCP session.";
        return false;
    }

    private static bool TryTask(McpEnvironment env, JsonObject? args, out Guid taskId, out string error)
    {
        var raw = Text(args, "taskId");
        if (string.IsNullOrWhiteSpace(raw))
            raw = env.Get("BEACON_TASK_ID");
        if (Guid.TryParse(raw, out taskId))
        {
            error = "";
            return true;
        }

        taskId = Guid.Empty;
        error = "missing 'taskId' (or set BEACON_TASK_ID).";
        return false;
    }

    private static bool TryGuid(JsonObject? args, string key, out Guid id)
        => Guid.TryParse(Text(args, key), out id);

    private static bool TryGuidList(JsonObject? args, string key, out List<Guid> ids, out string error)
    {
        ids = [];
        var node = args?[key];
        if (node is null)
        {
            error = $"missing '{key}'";
            return false;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null;
                if (!Guid.TryParse(text, out var id))
                {
                    error = $"invalid guid in '{key}'";
                    return false;
                }

                ids.Add(id);
            }

            error = "";
            return true;
        }

        var raw = Text(args, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"missing '{key}'";
            return false;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id))
            {
                error = $"invalid guid in '{key}'";
                return false;
            }

            ids.Add(id);
        }

        error = "";
        return true;
    }

    private static string? Text(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null || node.GetValueKind() != JsonValueKind.String)
            return null;
        return node.GetValue<string>();
    }

    private static bool? Flag(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null)
            return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? IntArg(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null)
            return null;
        if (node.GetValueKind() == JsonValueKind.Number && node is JsonValue number && number.TryGetValue<int>(out var value))
            return value;
        if (node.GetValueKind() == JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var parsed))
            return parsed;
        return null;
    }

    private static IReadOnlyList<string> Strings(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }

        var text = Text(args, key);
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonArray StringArray(IReadOnlyList<string> values)
        => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());

    private static string? RoleName(string? raw) => raw?.ToLowerInvariant() switch
    {
        "planner" => "Planner",
        "actor" => "Actor",
        "review" => "Review",
        _ => null
    };

    private static void Put(JsonObject body, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[key] = value;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => McpSchema.Tool(name, description, schema);

    private static JsonObject Props(params (string Name, string Type, bool Required)[] fields) => McpSchema.Props(fields);

    private static JsonObject FinishSchema()
    {
        var schema = Props(
            ("taskId", "string", false),
            ("result", "string", true),
            ("output", "string", false),
            ("reviewTranscriptRef", "string", false),
            ("reviewRunId", "string", false));
        var properties = schema["properties"]!.AsObject();
        properties["review"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reviewerRun"] = new JsonObject { ["type"] = "boolean" },
                ["regressionsFound"] = new JsonObject { ["type"] = "integer" },
                ["regressionsFixed"] = new JsonObject { ["type"] = "integer" }
            }
        };
        return schema;
    }
}