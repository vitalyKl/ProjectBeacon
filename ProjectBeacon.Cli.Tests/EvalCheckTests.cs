using ProjectBeacon.Cli.Client;

namespace ProjectBeacon.Cli.Tests;

public sealed class EvalCheckTests
{
    [Fact]
    public void Execute_ZeroExit_IsSuccess()
    {
        var result = EvalCheck.Execute(Path.GetTempPath(), "exit 0");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Execute_NonZeroExit_IsFailure()
    {
        var result = EvalCheck.Execute(Path.GetTempPath(), "exit 3");
        Assert.Equal(3, result.ExitCode);
    }
}
