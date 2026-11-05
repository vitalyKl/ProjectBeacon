namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using ProjectBeacon.Cli.Client;

public sealed class ProcessJobTests
{
    [Fact]
    public void TryAssign_AssignsRunningProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start());
        try
        {
            Assert.True(ProcessJob.TryAssign(process));
            Assert.True(ProcessJob.IsActive);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    [Fact]
    public async Task ProcessRunnerClosesRedirectedStdinForNonInteractiveCommands()
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("hash-object");
        start.ArgumentList.Add("--stdin");

        var result = await ProcessRunner.RunAsync(
            start,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", result.StdOut.Trim());
    }

    [Fact]
    public void TryStart_AssignsJobToLaunchedProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var launched = ProcessRunner.TryStart(psi);
        Assert.Equal(ProcessLaunchError.None, launched.Error);
        if (launched.Process is not { } process)
            return;
        try
        {
            process.WaitForExit(5000);
        }
        finally
        {
            process.Dispose();
        }
    }
}
