using System.Diagnostics;

namespace ProjectBeacon.Cli.Client;

internal static class ProcessControl
{
    internal static bool TryBeginDrain(Process process, Action<string>? onStdoutLine = null, Action<string>? onStderrLine = null, Action<Process>? onExit = null)
    {
        try
        {
            if (onStdoutLine is not null)
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        onStdoutLine(e.Data);
                };
            if (onStderrLine is not null)
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        onStderrLine(e.Data);
                };
            if (onExit is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => onExit(process);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            return false;
        }
    }

    internal static async Task KillAsync(Process process, TimeSpan? timeout = null)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            using var wait = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token);
        }
        catch
        {
        }

        try { process.CancelOutputRead(); } catch { }
        try { process.CancelErrorRead(); } catch { }
    }

    /// <summary>
    /// The PID of the process listening on the TCP port (via netstat), or 0 when nothing listens or the lookup fails.
    /// </summary>
    internal static int FindListeningPortPid(int port)
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return 0;
            try
            {
                using var output = process.StandardOutput;
                var marker = $":{port}";
                while (output.ReadLine() is { } line)
                {
                    if (!line.Contains(marker, StringComparison.Ordinal) || !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return tokens.Length >= 2 && int.TryParse(tokens[^1], out var pid) ? pid : 0;
                }
            }
            finally
            {
                if (!process.WaitForExit(3000))
                    try { process.Kill(); } catch { }
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
