using System.Diagnostics;

namespace ProjectBeacon.Cli.Client;

public enum ProcessLaunchError
{
    None,
    NotStarted,
    StartThrew,
    Drain
}

public readonly record struct LaunchedProcess(Process? Process, ProcessLaunchError Error, string? ExceptionMessage);

public static class ProcessRunner
{
    public static LaunchedProcess TryStart(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return new LaunchedProcess(null, ProcessLaunchError.NotStarted, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            return new LaunchedProcess(null, ProcessLaunchError.StartThrew, ex.Message);
        }

        if (!ProcessControl.TryBeginDrain(process))
        {
            process.Dispose();
            return new LaunchedProcess(null, ProcessLaunchError.Drain, null);
        }

        return new LaunchedProcess(process, ProcessLaunchError.None, null);
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunAsync(
        ProcessStartInfo info,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        TimeSpan? outputDrainTimeout = null,
        TimeSpan? killWait = null,
        int timeoutExitCode = 124)
    {
        var drainMs = outputDrainTimeout is { } drain ? (int)drain.TotalMilliseconds : 10_000;
        var afterKill = killWait ?? TimeSpan.FromSeconds(5);

        using var process = new Process { StartInfo = info };
        try
        {
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
                    await WaitForExitBounded(process, afterKill);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    Observe(stdoutTask);
                    Observe(stderrTask);
                    throw;
                }
            }
            else
            {
                await process.WaitForExitAsync(ct);
            }

            var stdout = await SafeRead(stdoutTask, drainMs);
            var stderr = await SafeRead(stderrTask, drainMs);
            int exitCode = 0;
            if (!timedOut)
            {
                try { exitCode = process.ExitCode; } catch { }
            }
            else
            {
                exitCode = timeoutExitCode;
            }
            return (exitCode, stdout, stderr, timedOut);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private static async Task WaitForExitBounded(Process process, TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
            return;

        try
        {
            using var cts = new CancellationTokenSource(budget);
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
        }
    }

    private static async Task<string> SafeRead(Task<string> task, int drainMs)
    {
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(drainMs));
            if (completed == task && task.IsCompletedSuccessfully)
                return task.Result;
        }
        catch { }
        Observe(task);
        return "";
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
