namespace ProjectBeacon.Cli.Client;

using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Context;

public sealed record OpenCodeApplyResult(string ConfigPath, Dictionary<string, string> Environment);

public static class OpencodeConfig
{
    public static void Upsert(
        string path,
        JsonElement mcp,
        string? model,
        JsonElement agent,
        bool denyNativeFiles,
        JsonElement provider = default,
        bool replaceMcp = false,
        string[]? instructions = null)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        root["$schema"] = "https://opencode.ai/config.json";

        if (mcp.ValueKind == JsonValueKind.Object)
        {
            var mcpObj = replaceMcp ? new JsonObject() : root["mcp"] as JsonObject ?? new JsonObject();
            foreach (var prop in mcp.EnumerateObject())
                mcpObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            root["mcp"] = mcpObj;
        }

        if (!string.IsNullOrWhiteSpace(model))
            root["model"] = model;

        if (agent.ValueKind == JsonValueKind.Object)
        {
            var agentObj = root["agent"] as JsonObject ?? new JsonObject();
            foreach (var prop in agent.EnumerateObject())
                agentObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            root["agent"] = agentObj;
        }

        if (provider.ValueKind == JsonValueKind.Object)
        {
            var providerObj = root["provider"] as JsonObject ?? new JsonObject();
            foreach (var prop in provider.EnumerateObject())
                providerObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            root["provider"] = providerObj;
        }

        if (instructions is { Length: > 0 })
        {
            var existing = root["instructions"] as JsonArray ?? new JsonArray();
            var existingSet = existing.Select(j => j!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var instr in instructions)
                if (existingSet.Add(instr))
                    existing.Add(instr);
            root["instructions"] = existing;
        }

        if (denyNativeFiles)
        {
            var permission = root["permission"] as JsonObject ?? new JsonObject();
            permission["read"] = "deny";
            permission["edit"] = "deny";
            permission["glob"] = "deny";
            permission["grep"] = "deny";
            root["permission"] = permission;
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static OpenCodeApplyResult ApplyOpenCodeConnections(string json, string? configPath = null)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("value", out var wrapped) ? wrapped : doc.RootElement;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = configPath ?? Path.Combine(home, ".config", "opencode", "opencode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonObject root;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            root = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }
        root["$schema"] = "https://opencode.ai/config.json";
        var providers = root["provider"] as JsonObject ?? new JsonObject();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var provider = row.TryGetProperty("providerId", out var p) ? p.GetString() : null;
                var model = row.TryGetProperty("modelId", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                    continue;
                var block = providers[provider] as JsonObject ?? new JsonObject();
                var baseUrl = row.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    block["npm"] = "@ai-sdk/openai-compatible";
                    var options = block["options"] as JsonObject ?? new JsonObject();
                    options["baseURL"] = baseUrl;
                    block["options"] = options;
                }
                var apiKey = row.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var envName = "BEACON_OC_" + new string(provider.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    env[envName] = apiKey;
                    var options = block["options"] as JsonObject ?? new JsonObject();
                    options["apiKey"] = "{env:" + envName + "}";
                    block["options"] = options;
                }
                var models = block["models"] as JsonObject ?? new JsonObject();
                models[model] = new JsonObject { ["name"] = model };
                block["models"] = models;
                providers[provider] = block;
            }
        }
        root["provider"] = providers;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new OpenCodeApplyResult(path, env);
    }

    public static Result<string> ApplyOpencode(string root, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("missing root");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var payloadRoot = doc.RootElement;
        var path = payloadRoot.TryGetProperty("path", out var p) ? p.GetString() : null;
        var resolved = WorkspacePath.ResolveInRoot(root, path, relativeOnly: true);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "path is not inside root.");

        var full = resolved.Value!;
        var opencodePath = Path.Combine(full, "opencode.json");
        var mcp = ReplaceBeaconCommand(payloadRoot.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default);
        var model = payloadRoot.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
        var agent = payloadRoot.TryGetProperty("agent", out var agentEl) ? agentEl : default;
        var provider = payloadRoot.TryGetProperty("provider", out var providerEl) ? providerEl : default;
        var replaceMcp = payloadRoot.TryGetProperty("mcpReplace", out var replaceEl) && replaceEl.ValueKind == JsonValueKind.True;
        WriteToolDisciplineInstructions(full);
        Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider, replaceMcp,
            instructions: new[] { BeaconToolDiscipline.RelativePath });
        return Result.Ok(JsonSerializer.Serialize(new { path = opencodePath }));
    }

    public static JsonElement ReplaceBeaconCommand(JsonElement mcp, string? beaconPath = null, string? settingsPath = null)
    {
        if (mcp.ValueKind != JsonValueKind.Object)
            return mcp;
        var resolved = !string.IsNullOrWhiteSpace(beaconPath) ? beaconPath : ResolveBeaconPath(settingsPath);
        if (string.IsNullOrWhiteSpace(resolved))
            return mcp;

        var node = JsonNode.Parse(mcp.GetRawText()) as JsonObject;
        if (node is null)
            return mcp;

        var changed = false;
        foreach (var server in node)
        {
            if (server.Value is not JsonObject serverObj || serverObj["command"] is not JsonArray command || command.Count == 0)
                continue;
            if (command[0] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                continue;
            var first = value.GetValue<string>();
            if (first is not null
                && !first.Contains('/')
                && !first.Contains('\\')
                && string.Equals(first, "beacon", StringComparison.OrdinalIgnoreCase))
            {
                command[0] = resolved;
                changed = true;
            }
        }
        return changed ? JsonSerializer.SerializeToElement(node) : mcp;
    }

    public static string? ResolveBeaconPath(string? settingsPath = null)
    {
        var (settings, _) = WorkstationSettings.TryRead(settingsPath);
        // A saved override pointing at a build that no longer exists (stale dev path)
        // must not shadow the running package binary.
        return !string.IsNullOrWhiteSpace(settings.BeaconPath) && File.Exists(settings.BeaconPath)
            ? settings.BeaconPath
            : Environment.ProcessPath;
    }

    private static void WriteToolDisciplineInstructions(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, ".opencode", "instructions");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "beacon-tool-discipline.md");
        File.WriteAllText(file, BeaconToolDiscipline.Rules);
    }
}
