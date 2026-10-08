using System.Diagnostics;

namespace ProjectBeacon.Cli.Client;

public static class ProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunAsync(
        ProcessStartInfo info, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            return (1, "", "failed to start", false);

        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd(), CancellationToken.None);
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd(), CancellationToken.None);

        bool timedOut = false;
        if (timeout is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout.Value);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
        else
        {
            await process.WaitForExitAsync(ct);
        }

        var stdout = await SafeRead(stdoutTask);
        var stderr = await SafeRead(stderrTask);
        int exitCode = 0;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; } catch { }
        }
        else
        {
            exitCode = 124;
        }
        return (exitCode, stdout, stderr, timedOut);
    }

    private static async Task<string> SafeRead(Task<string> task)
    {
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(10000));
            if (completed == task && task.IsCompletedSuccessfully)
                return task.Result;
        }
        catch { }
        return "";
    }
}
