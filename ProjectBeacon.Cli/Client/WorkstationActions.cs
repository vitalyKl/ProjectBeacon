namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Common;

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
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("missing root");

        if (string.IsNullOrWhiteSpace(path))
            path = root;

        var resolved = WorkspacePath.ValidateAbsoluteInsideRoot(root, path);
        if (!resolved.Success)
            return resolved;

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

    public static Result<string> ScanGguf(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("missing root");

        if (string.IsNullOrWhiteSpace(path))
            path = root;

        var resolved = WorkspacePath.ValidateAbsoluteInsideRoot(root, path);
        if (!resolved.Success)
            return resolved;

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
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<string>("path is required.");

        var resolved = WorkspacePath.ValidateAbsoluteInsideRoot(root, path);
        if (!resolved.Success)
            return resolved;

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
        var mcp = payloadRoot.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default;
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

    public static Result<string> ApplyOpencode(string root, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("missing root");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var payloadRoot = doc.RootElement;
        var path = payloadRoot.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<string>("path is required.");

        var resolved = WorkspacePath.ValidateAbsoluteInsideRoot(root, path);
        if (!resolved.Success)
            return resolved;

        var full = resolved.Value!;
        var opencodePath = Path.Combine(full, "opencode.json");
        var mcp = payloadRoot.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default;
        var model = payloadRoot.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
        var agent = payloadRoot.TryGetProperty("agent", out var agentEl) ? agentEl : default;
        var provider = payloadRoot.TryGetProperty("provider", out var providerEl) ? providerEl : default;
        var replaceMcp = payloadRoot.TryGetProperty("mcpReplace", out var replaceEl) && replaceEl.ValueKind == JsonValueKind.True;
        OpencodeConfig.Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider, replaceMcp);
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
}

public static class OpencodeConfig
{
    public static void Upsert(
        string path,
        JsonElement mcp,
        string? model,
        JsonElement agent,
        bool denyNativeFiles,
        JsonElement provider = default,
        bool replaceMcp = false)
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
