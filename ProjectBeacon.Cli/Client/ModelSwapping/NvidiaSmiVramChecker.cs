using System.Diagnostics;
using System.Globalization;

namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Queries <c>nvidia-smi</c> for free VRAM. Returns -1 if the tool or GPU is unavailable.
/// </summary>
public sealed class NvidiaSmiVramChecker : IVramChecker
{
    public async Task<long> GetFreeVramMbAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=memory.free --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return -1;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                return -1;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return -1;
            return long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : -1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return -1;
        }
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
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-compute-apps=pid,used_gpu_memory --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return 0;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                return 0;
            return ParseProcessMb(output, pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }
}
