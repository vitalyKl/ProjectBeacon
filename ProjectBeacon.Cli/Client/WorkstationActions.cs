namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Context;
using ProjectBeacon.Application.Runtime;

public static class WorkstationActions
{
    public static string ProbeJson(object? llamaSwapStatus = null, object? hostLoad = null, object? opencodeServe = null)
    {
        var probe = new Dictionary<string, object?>
        {
            ["git"] = Which("git"),
            ["opencode"] = Which("opencode"),
            ["llamaServer"] = Which("llama-server") ?? Which("llama-server.exe"),
            ["llamaSwap"] = Which("llama-swap") ?? Which("llama-swap.exe"),
            ["node"] = Which("node"),
            ["docker"] = Which("docker"),
            ["dotnet"] = Which("dotnet"),
            ["os"] = Environment.OSVersion.ToString(),
            ["machine"] = Environment.MachineName,
            ["clientVersion"] = typeof(WorkstationActions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(WorkstationActions).Assembly.GetName().Version?.ToString()
                ?? "0"
        };
        if (llamaSwapStatus is not null)
            probe["llamaSwapStatus"] = llamaSwapStatus;
        if (hostLoad is not null)
            probe["hostLoad"] = hostLoad;
        if (opencodeServe is not null)
            probe["opencodeServe"] = opencodeServe;
        return JsonSerializer.Serialize(probe);
    }

    public static Result<string> ListDir(string root, string? path)
    {
        var resolved = WorkspacePath.ResolveInRoot(root, path, relativeOnly: false);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "missing root");

        var full = resolved.Value!;
        if (!Directory.Exists(full))
            return Result.Failure<string>($"directory not found: {full}");

        var parent = Directory.GetParent(full)?.FullName;
        var entries = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                entries.Add(new { name = Path.GetFileName(dir), path = dir, isDirectory = true });
            }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var file in Directory.EnumerateFiles(full).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(200))
        {
            entries.Add(new { name = Path.GetFileName(file), path = file, isDirectory = false });
        }
        return Result.Ok(JsonSerializer.Serialize(new { path = full, parent, entries }));
    }

    public static async Task<Result<string>> RunEvalTurnAsync(
        string root,
        string payloadJson,
        ClientOpenCodeServe openCode,
        IAgentRuntime runtime,
        TimeSpan pollInterval,
        TimeSpan idleTimeout,
        TimeSpan maxDuration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("root is required.");
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Result.Failure<string>("payload is required.");

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<string>("payload must be valid JSON.");
        }

        var evalRunId = payload.TryGetProperty("evalRunId", out var runIdElement) && runIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(runIdElement.GetString(), out var runId)
                ? runId
                : Guid.Empty;
        if (evalRunId == Guid.Empty)
            return Result.Failure<string>("evalRunId is required.");

        var prompt = payload.TryGetProperty("prompt", out var promptElement) ? promptElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return Result.Failure<string>("prompt is required.");

        var relativePath = payload.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var validatedPath = WorkspacePath.ResolveInRoot(root, relativePath, relativeOnly: true);
        if (!validatedPath.Success)
            return Result.Failure<string>(validatedPath.Error ?? "path is not inside root.");

        var model = payload.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
        var title = payload.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : "Beacon eval";
        var controls = payload.TryGetProperty("controls", out var controlsElement) && controlsElement.ValueKind == JsonValueKind.Object
            ? controlsElement
            : default;
        if (controls.ValueKind == JsonValueKind.Object)
        {
            if (controls.TryGetProperty("model", out var pinnedModel) && pinnedModel.ValueKind == JsonValueKind.String)
            {
                var pinned = pinnedModel.GetString();
                if (!string.IsNullOrWhiteSpace(pinned))
                    model = pinned;
            }
            if (controls.TryGetProperty("timeoutSeconds", out var timeoutElement)
                && timeoutElement.TryGetInt32(out var timeoutSeconds)
                && timeoutSeconds > 0)
                maxDuration = TimeSpan.FromSeconds(timeoutSeconds);
            if (controls.TryGetProperty("repoRevision", out var revisionElement))
            {
                var expected = revisionElement.GetString();
                if (!string.IsNullOrWhiteSpace(expected))
                {
                    var head = GitHead(validatedPath.Value!);
                    if (!string.Equals(head, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                        return Result.Failure<string>("Repository revision does not match the eval pin.");
                }
            }
        }

        await openCode.TickAsync(validatedPath.Value, ct);
        if (!openCode.Status.Healthy)
            return Result.Failure<string>(openCode.Status.Error ?? "OpenCode is not running.");

        var sessionId = await runtime.CreateSessionAsync(string.IsNullOrWhiteSpace(title) ? "Beacon eval" : title, ct);
        await runtime.SendPromptAsync(sessionId, prompt, model, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(idleTimeout / pollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(maxDuration / pollInterval));
        var quiet = 0;
        var idle = false;
        for (var i = 0; i < maxIterations && !ct.IsCancellationRequested; i++)
        {
            var parts = new List<AgentMessagePart>();
            await foreach (var part in runtime.StreamPartsAsync(sessionId, ct))
                parts.Add(part);
            var added = 0;
            foreach (var part in parts)
            {
                if (string.Equals(part.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                var key = part.ExternalId ?? $"{part.Role}|{part.Kind}|{part.Body}";
                if (seen.Add(key))
                    added++;
            }
            quiet = added > 0 ? 0 : quiet + 1;
            if (quiet >= maxQuiet && seen.Count > 0)
            {
                idle = true;
                break;
            }
            await Task.Delay(pollInterval, ct);
        }

        var usage = await runtime.ReadUsageAsync(sessionId, ct);
        bool? passed = null;
        string? checkOutput = null;
        if (idle && seen.Count > 0)
        {
            var check = payload.TryGetProperty("checkCommand", out var checkElement) ? checkElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(check))
            {
                var checkResult = EvalCheck.Execute(validatedPath.Value!, check);
                passed = checkResult.ExitCode == 0;
                checkOutput = checkResult.Output;
            }
        }
        var result = new
        {
            evalRunId,
            sessionId,
            promptTokens = usage.PromptTokens,
            completionTokens = usage.CompletionTokens,
            turnCount = Math.Max(usage.AssistantMessages, seen.Count),
            passed,
            checkOutput,
            interrupted = !idle,
            transcriptRef = $"opencode:session/{sessionId}"
        };
        return Result.Ok(JsonSerializer.Serialize(result));
    }

    private static string? GitHead(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (!process.Start())
                return null;
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            if (process.ExitCode != 0)
                return null;
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Result<string> ScanGguf(string root, string? path)
    {
        var resolved = WorkspacePath.ResolveInRoot(root, path, relativeOnly: false);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "missing root");

        var full = resolved.Value!;
        if (!Directory.Exists(full))
            return Result.Ok(JsonSerializer.Serialize(new { root = full, files = Array.Empty<object>() }));

        var files = Directory.EnumerateFiles(full, "*.gguf", SearchOption.AllDirectories)
            .Take(200)
            .Select(f => new { name = Path.GetFileName(f), path = f, bytes = new FileInfo(f).Length })
            .ToList();
        return Result.Ok(JsonSerializer.Serialize(new { root = full, files }));
    }

    public static Result<string> InitProject(string root, string payloadJson)
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
        Directory.CreateDirectory(full);

        if (payloadRoot.TryGetProperty("createGit", out var gitEl) && gitEl.ValueKind == JsonValueKind.True
            && !Directory.Exists(Path.Combine(full, ".git")))
        {
            Run("git", "init", full);
        }

        var gitignore = Path.Combine(full, ".gitignore");
        MergeGitignore(gitignore);

        var historyInProject = !payloadRoot.TryGetProperty("historyInProject", out var hist) || hist.ValueKind != JsonValueKind.False;
        var dataDir = Path.Combine(full, ".opencode", "data");
        if (historyInProject)
            Directory.CreateDirectory(dataDir);

        var opencodePath = Path.Combine(full, "opencode.json");
        var mcp = ReplaceBeaconCommand(payloadRoot.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default);
        var model = payloadRoot.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
        var agent = payloadRoot.TryGetProperty("agent", out var agentEl) ? agentEl : default;
        var provider = payloadRoot.TryGetProperty("provider", out var providerEl) ? providerEl : default;
        OpencodeConfig.Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider);

        var local = Path.Combine(full, ".opencode", "local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        if (payloadRoot.TryGetProperty("localEnv", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            File.WriteAllText(local, envEl.GetRawText());

        return Result.Ok(JsonSerializer.Serialize(new { path = full, gitignore, opencode = opencodePath, historyDir = historyInProject ? dataDir : null }));
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
        OpencodeConfig.Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider, replaceMcp,
            instructions: new[] { BeaconToolDiscipline.RelativePath });
        return Result.Ok(JsonSerializer.Serialize(new { path = opencodePath }));
    }

    public static string SaveWorkstation(string payloadJson, string? settingsPath = null)
    {
        var (settings, _) = WorkstationSettings.TryRead(settingsPath);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("modelsRoot", out var models) && models.ValueKind == JsonValueKind.String)
            settings.ModelsRoot = models.GetString();
        if (root.TryGetProperty("llamaCppBin", out var llama) && llama.ValueKind == JsonValueKind.String)
            settings.LlamaCppBin = llama.GetString();
        if (root.TryGetProperty("llamaSwapBin", out var swap) && swap.ValueKind == JsonValueKind.String)
            settings.LlamaSwapBin = swap.GetString();
        if (root.TryGetProperty("llamaSwapPort", out var port) && port.TryGetInt32(out var p) && p > 0)
            settings.LlamaSwapPort = p;
        if (root.TryGetProperty("useOwnSwapper", out var own) &&
            (own.ValueKind == JsonValueKind.True || own.ValueKind == JsonValueKind.False))
            settings.UseOwnSwapper = own.GetBoolean();
        if (root.TryGetProperty("concurrentPortBase", out var cport) && cport.TryGetInt32(out var cp) && cp > 0)
            settings.ConcurrentPortBase = cp;
        if (root.TryGetProperty("opencodeDataDir", out var data) && data.ValueKind == JsonValueKind.String)
            settings.OpencodeDataDir = data.GetString();
        if (root.TryGetProperty("projectsRoot", out var projects) && projects.ValueKind == JsonValueKind.String)
            settings.ProjectsRoot = projects.GetString();
        settings.Save(settingsPath);
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public static string Install(string payloadJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("id is required.");

        var wingetId = id.Trim().ToLowerInvariant() switch
        {
            "git" => "Git.Git",
            "node" => "OpenJS.NodeJS.LTS",
            "docker" => "Docker.DockerDesktop",
            _ => null
        };
        if (wingetId is null)
            throw new InvalidOperationException($"Install of '{id}' is not in the allowlist. Install it on the machine, then re-run probe.");

        var winget = Which("winget");
        if (winget is null)
            throw new InvalidOperationException("winget is not available.");

        var (code, output) = RunCaptured(winget, $"install -e --id {wingetId} --accept-package-agreements --accept-source-agreements");
        if (code != 0)
            throw new InvalidOperationException(output);
        return JsonSerializer.Serialize(new { id, wingetId, output });
    }

    public static void MergeGitignore(string path)
    {
        var required = new[]
        {
            ".opencode/data/",
            ".opencode/*.local.json",
            ".env",
            "*.gguf"
        };
        var existing = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.TrimEnd()).ToList()
            : [];
        var set = new HashSet<string>(existing, StringComparer.Ordinal);
        foreach (var line in required)
        {
            if (!set.Contains(line))
                existing.Add(line);
        }
        File.WriteAllLines(path, existing);
    }

    public static string? ResolveBeaconPath(string? settingsPath = null)
    {
        var (settings, _) = WorkstationSettings.TryRead(settingsPath);
        return !string.IsNullOrWhiteSpace(settings.BeaconPath) ? settings.BeaconPath : Environment.ProcessPath;
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

    public static string? Which(string name, string? path = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (path is not null)
                psi.Environment["PATH"] = path;
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return null;
            var lines = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0)
                return null;
            if (!OperatingSystem.IsWindows())
                return lines[0];
            // `where` lists an extensionless POSIX shim before its .cmd twin in the same
            // directory (npm packages); Process.Start can only launch .exe/.cmd/.bat.
            return lines
                .Select((line, index) => (line, index, rank: LaunchRank(line)))
                .OrderBy(x => x.rank)
                .ThenBy(x => x.index)
                .First()
                .line;
        }
        catch
        {
            return null;
        }
    }

    private static int LaunchRank(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".exe")
            return 0;
        if (ext is ".cmd" or ".bat")
            return 1;
        return 2;
    }

    private static void Run(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
        if (proc is { ExitCode: not 0 })
            throw new InvalidOperationException($"{file} {args} exited {proc.ExitCode}");
    }

    private static (int Code, string Output) RunCaptured(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return (1, "failed to start");
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }

    private static void WriteToolDisciplineInstructions(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, ".opencode", "instructions");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "beacon-tool-discipline.md");
        File.WriteAllText(file, BeaconToolDiscipline.Rules);
    }
}

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
            var existingSet = existing.Select(j => j.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
}
