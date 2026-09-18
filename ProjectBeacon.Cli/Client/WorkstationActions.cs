namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class WorkstationActions
{
    public static string ProbeJson(object? llamaSwapStatus = null)
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
            ["machine"] = Environment.MachineName
        };
        if (llamaSwapStatus is not null)
            probe["llamaSwapStatus"] = llamaSwapStatus;
        return JsonSerializer.Serialize(probe);
    }

    public static string ListDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (OperatingSystem.IsWindows())
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new { name = d.Name, path = d.RootDirectory.FullName, isDirectory = true })
                    .ToList();
                return JsonSerializer.Serialize(new { path = "", parent = (string?)null, entries = drives });
            }
            path = "/";
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

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
        return JsonSerializer.Serialize(new { path = full, parent, entries });
    }

    public static string ScanGguf(string? modelsRoot)
    {
        var root = string.IsNullOrWhiteSpace(modelsRoot)
            ? WorkstationSettings.Load().ModelsRoot ?? WorkstationSettings.DefaultModelsRoot
            : modelsRoot;
        if (!Directory.Exists(root))
            return JsonSerializer.Serialize(new { root, files = Array.Empty<object>() });

        var files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories)
            .Take(200)
            .Select(f => new { name = Path.GetFileName(f), path = f, bytes = new FileInfo(f).Length })
            .ToList();
        return JsonSerializer.Serialize(new { root, files });
    }

    public static string InitProject(string payloadJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var root = doc.RootElement;
        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("path is required.");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);

        if (root.TryGetProperty("createGit", out var gitEl) && gitEl.ValueKind == JsonValueKind.True
            && !Directory.Exists(Path.Combine(full, ".git")))
        {
            Run("git", "init", full);
        }

        var gitignore = Path.Combine(full, ".gitignore");
        MergeGitignore(gitignore);

        var historyInProject = !root.TryGetProperty("historyInProject", out var hist) || hist.ValueKind != JsonValueKind.False;
        var dataDir = Path.Combine(full, ".opencode", "data");
        if (historyInProject)
            Directory.CreateDirectory(dataDir);

        var opencodePath = Path.Combine(full, "opencode.json");
        var mcp = root.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default;
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
        var agent = root.TryGetProperty("agent", out var agentEl) ? agentEl : default;
        var provider = root.TryGetProperty("provider", out var providerEl) ? providerEl : default;
        OpencodeConfig.Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider);

        var local = Path.Combine(full, ".opencode", "local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        if (root.TryGetProperty("localEnv", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            File.WriteAllText(local, envEl.GetRawText());

        return JsonSerializer.Serialize(new { path = full, gitignore, opencode = opencodePath, historyDir = historyInProject ? dataDir : null });
    }

    public static string ApplyOpencode(string payloadJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        var root = doc.RootElement;
        var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("path is required.");
        var opencodePath = Path.Combine(Path.GetFullPath(path), "opencode.json");
        var mcp = root.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default;
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
        var agent = root.TryGetProperty("agent", out var agentEl) ? agentEl : default;
        var provider = root.TryGetProperty("provider", out var providerEl) ? providerEl : default;
        OpencodeConfig.Upsert(opencodePath, mcp, model, agent, denyNativeFiles: true, provider);
        return JsonSerializer.Serialize(new { path = opencodePath });
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

    public static string? Which(string name)
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
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return null;
            var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null;
        }
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
        JsonElement provider = default)
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
            var mcpObj = root["mcp"] as JsonObject ?? new JsonObject();
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
