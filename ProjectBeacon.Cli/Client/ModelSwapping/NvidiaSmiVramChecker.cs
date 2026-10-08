using System.Diagnostics;
using System.Globalization;
using ProjectBeacon.Cli.Client;

namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Queries <c>nvidia-smi</c> for free VRAM. Missing tool is unavailable, not a failed query.
/// </summary>
public sealed class NvidiaSmiVramChecker : IVramChecker
{
    public async Task<VramReading> ReadFreeAsync(CancellationToken ct)
    {
        var run = await NvidiaSmiRunner.RunAsync(
            "nvidia-smi",
            "--query-gpu=memory.free --format=csv,noheader,nounits",
            ct);
        if (!run.Found)
            return new VramReading(-1, false);
        if (run.TimedOut || run.ExitCode != 0)
            return new VramReading(-1, true);
        var line = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null || !long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
            return new VramReading(-1, true);
        return new VramReading(mb, false);
    }

    public static long ParseProcessMb(string stdout, int pid)
    {
        long total = 0;
        var found = false;
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(',');
            if (parts.Length < 2)
                continue;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowPid) || rowPid != pid)
                continue;
            if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
                continue;
            found = true;
            total += mb;
        }

        return found ? total : 0;
    }

    public static async Task<long> QueryProcessMbAsync(int pid, CancellationToken ct)
    {
        var run = await NvidiaSmiRunner.RunAsync(
            "nvidia-smi",
            "--query-compute-apps=pid,used_gpu_memory --format=csv,noheader,nounits",
            ct);
        if (!run.Found || run.TimedOut || run.ExitCode != 0)
            return 0;
        return ParseProcessMb(run.Stdout, pid);
    }
}

internal static class NvidiaSmiRunner
{
    internal readonly record struct Result(bool Found, bool TimedOut, int ExitCode, string Stdout);

    internal static async Task<Result> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            var (code, stdout, stderr, timedOut) = await ProcessRunner.RunAsync(
                psi,
                TimeSpan.FromSeconds(2),
                ct,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                -1);
            if (!timedOut && code == 1 && stdout.Length == 0 && stderr == "failed to start")
                return new Result(false, false, -1, "");
            if (timedOut)
                return new Result(true, true, -1, "");
            return new Result(true, false, code, stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new Result(false, false, -1, "");
        }
    }
}
