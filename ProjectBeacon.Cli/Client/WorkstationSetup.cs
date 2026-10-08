namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using ProjectBeacon.Application.Common;

public static class WorkstationSetup
{
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
        var mcp = OpencodeConfig.ReplaceBeaconCommand(payloadRoot.TryGetProperty("mcp", out var mcpEl) ? mcpEl : default);
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

        var winget = WorkstationProbe.Which("winget");
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
        var (code, _, _, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        if (timedOut)
            throw new InvalidOperationException($"{file} {args} timed out");
        if (code != 0)
            throw new InvalidOperationException($"{file} {args} exited {code}");
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
        var (code, stdout, stderr, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        if (timedOut)
            return (-1, "timed out");
        var output = (stdout + stderr).Trim();
        return (code, output);
    }
}
