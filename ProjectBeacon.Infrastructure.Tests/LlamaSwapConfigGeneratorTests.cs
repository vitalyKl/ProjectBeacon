namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.LlamaSwap;

public sealed class LlamaSwapConfigGeneratorTests
{
    [Fact]
    public void Generate_EmptyRegistry_ReturnsEmptyModels()
    {
        Assert.Equal("models: {}\n", LlamaSwapConfigGenerator.Generate([]));
    }

    [Fact]
    public void Generate_SimpleModel_EmitsCmdAndTtl()
    {
        var config = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m qwen.gguf", 4096, 300, [])]);

        Assert.Equal(
            "models:\n" +
            "  qwen:\n" +
            "    cmd: \"llama-server -m qwen.gguf --ctx-size 4096\"\n" +
            "    ttl: 300\n",
            config);
    }

    [Fact]
    public void Generate_ZeroTtl_OmitsTtlLine()
    {
        var config = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m qwen.gguf", 0, 0, [])]);

        Assert.DoesNotContain("ttl", config);
        Assert.Equal("models:\n  qwen:\n    cmd: \"llama-server -m qwen.gguf\"\n", config);
    }

    [Fact]
    public void BuildCommand_ExistingCtxSize_IsNotDuplicated()
    {
        var spec = new LlamaSwapModelSpec("m", "llama-server --ctx-size 2048 -m m.gguf", 4096, 0, []);

        Assert.Equal("llama-server --ctx-size 2048 -m m.gguf", LlamaSwapConfigGenerator.BuildCommand(spec));
    }

    [Fact]
    public void BuildCommand_ExtraFlags_AreAppendedAndTrimmed()
    {
        var spec = new LlamaSwapModelSpec("m", "llama-server", 0, 0, [" -ngl 99 ", "", "  "]);

        Assert.Equal("llama-server -ngl 99", LlamaSwapConfigGenerator.BuildCommand(spec));
    }

    [Fact]
    public void Generate_NameCollision_SuffixesDuplicateKeys()
    {
        var config = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("a", "one", 0, 0, []),
            new LlamaSwapModelSpec("a", "two", 0, 0, []),
            new LlamaSwapModelSpec("a", "three", 0, 0, [])]);

        Assert.Contains("  a:", config);
        Assert.Contains("  a-2:", config);
        Assert.Contains("  a-3:", config);
    }

    [Fact]
    public void Generate_UnsafeKey_IsDoubleQuoted()
    {
        var config = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("my model", "cmd with \"quotes\" and \\backslash", 0, 0, [])]);

        Assert.Contains("  \"my model\":", config);
        Assert.Contains("    cmd: \"cmd with \\\"quotes\\\" and \\\\backslash\"", config);
    }

    [Fact]
    public void Generate_SortsByNameForStableOutput()
    {
        var config = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("zeta", "z", 0, 0, []),
            new LlamaSwapModelSpec("alpha", "a", 0, 0, [])]);

        var alphaIdx = config.IndexOf("  alpha:", StringComparison.Ordinal);
        var zetaIdx = config.IndexOf("  zeta:", StringComparison.Ordinal);
        Assert.True(alphaIdx >= 0 && zetaIdx >= 0);
        Assert.True(alphaIdx < zetaIdx);
    }
}
