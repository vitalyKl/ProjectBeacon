namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;

public sealed class CommandLineSplitTests
{
    [Fact]
    public void Split_Empty_ReturnsEmptyExeAndArgs()
    {
        var (exe, args) = CommandLineSplit.Split("");
        Assert.Equal("", exe);
        Assert.Empty(args);
    }

    [Fact]
    public void Split_Null_ReturnsEmptyExeAndArgs()
    {
        var (exe, args) = CommandLineSplit.Split(null);
        Assert.Equal("", exe);
        Assert.Empty(args);
    }

    [Fact]
    public void Split_SimpleCommand_SplitsOnSpaces()
    {
        var (exe, args) = CommandLineSplit.Split("llama-server -m model.gguf --ctx-size 4096");
        Assert.Equal("llama-server", exe);
        Assert.Equal(["-m", "model.gguf", "--ctx-size", "4096"], args);
    }

    [Fact]
    public void Split_QuotedPathWithSpaces_PreservesSpace()
    {
        var (exe, args) = CommandLineSplit.Split("\"C:\\Program Files\\llama\\llama-server.exe\" -m m.gguf");
        Assert.Equal("C:\\Program Files\\llama\\llama-server.exe", exe);
        Assert.Equal(["-m", "m.gguf"], args);
    }

    [Fact]
    public void Split_QuotedArgWithSpaces()
    {
        var (exe, args) = CommandLineSplit.Split("app \"--flag value with spaces\" -x");
        Assert.Equal("app", exe);
        Assert.Equal(["--flag value with spaces", "-x"], args);
    }

    [Fact]
    public void Split_MultipleSpaces_Collapsed()
    {
        var (exe, args) = CommandLineSplit.Split("app   -a    -b");
        Assert.Equal("app", exe);
        Assert.Equal(["-a", "-b"], args);
    }

    [Fact]
    public void Split_Tabs_AreSeparators()
    {
        var (exe, args) = CommandLineSplit.Split("app\t-a\t-b");
        Assert.Equal("app", exe);
        Assert.Equal(["-a", "-b"], args);
    }

    [Fact]
    public void Split_EmptyQuotes_ProducesEmptyToken()
    {
        var (exe, args) = CommandLineSplit.Split("app \"\" -b");
        Assert.Equal("app", exe);
        Assert.Equal(["", "-b"], args);
    }

    [Fact]
    public void Split_SingleToken_NoArgs()
    {
        var (exe, args) = CommandLineSplit.Split("justexe");
        Assert.Equal("justexe", exe);
        Assert.Empty(args);
    }
}
