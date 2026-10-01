using System.Diagnostics;
using System.Globalization;

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
        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
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
                process = Process.Start(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                return new Result(false, false, -1, "");
            }

            if (process is null)
                return new Result(false, false, -1, "");

            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                await Drain(stdoutTask, stderrTask);
                return new Result(true, true, -1, "");
            }

            await Drain(stdoutTask, stderrTask);
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            return new Result(true, false, process.HasExited ? process.ExitCode : -1, stdout);
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
                Kill(process);
            if (stdoutTask is not null && stderrTask is not null)
                await Drain(stdoutTask, stderrTask);
            throw;
        }
        finally
        {
            if (process is { HasExited: false })
                Kill(process);
            process?.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task Drain(Task stdout, Task stderr)
    {
        var done = Task.WhenAll(stdout, stderr);
        var winner = await Task.WhenAny(done, Task.Delay(1000)).ConfigureAwait(false);
        if (winner != done)
            return;
        try
        {
            await done.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
