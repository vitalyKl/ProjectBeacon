using System.Diagnostics;

namespace ProjectBeacon.Cli.Client;

internal static class ProcessControl
{
    internal static bool TryBeginDrain(Process process)
    {
        try
        {
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
}
