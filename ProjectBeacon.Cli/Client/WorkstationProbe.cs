namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
/// <summary>
/// Builds heartbeat probeJson: tool paths, clientVersion from the assembly informational version, plus optional llama-swap, host load, and OpenCode status.
/// </summary>
public static class WorkstationProbe
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
            ["clientVersion"] = typeof(WorkstationProbe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(WorkstationProbe).Assembly.GetName().Version?.ToString()
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
            var (code, stdout, _, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (timedOut || code != 0)
                return null;
            var lines = stdout
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
}
