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

        var (code, stdout, stderr, timedOut) = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        if (timedOut)
            return (-1, "check timed out");

        var text = (stdout + stderr).Trim();
        if (text.Length > 4000)
            text = text[..4000];
        return (code, text);
    }
}
