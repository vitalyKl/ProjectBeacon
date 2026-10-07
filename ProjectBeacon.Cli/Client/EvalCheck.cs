namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;

internal static class EvalCheck
{
    internal static (int ExitCode, string Output) Execute(string workingDirectory, string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("check did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "check timed out");
        }

        var text = (ReadReady(stdout) + ReadReady(stderr)).Trim();
        if (text.Length > 4000)
            text = text[..4000];
        return (process.ExitCode, text);
    }

    private static string ReadReady(Task<string> read)
    {
        if (!read.Wait(2000) || !read.IsCompletedSuccessfully)
            return "";
        return read.Result;
    }
}
