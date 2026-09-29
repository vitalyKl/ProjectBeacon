namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.LlamaSwap;

public sealed class LlamaSwapConfigParserTests
{
    [Fact]
    public void Parse_EmptyYaml_ReturnsEmpty()
    {
        var result = LlamaSwapConfigParser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(LlamaSwapConfigParser.Parse(null!));
        Assert.Empty(LlamaSwapConfigParser.Parse("  \n\t\n"));
    }

    [Fact]
    public void Parse_SingleModel_ParsesNameCmdTtl()
    {
        var yaml = "models:\n" +
                   "  qwen:\n" +
                   "    cmd: \"llama-server -m qwen.gguf --ctx-size 4096\"\n" +
                   "    ttl: 300\n";

        var result = LlamaSwapConfigParser.Parse(yaml);

        var spec = Assert.Single(result);
        Assert.Equal("qwen", spec.Name);
        Assert.Equal("llama-server -m qwen.gguf --ctx-size 4096", spec.LaunchCommand);
        Assert.Equal(300, spec.Ttl);
        Assert.False(spec.Concurrent);
    }

    [Fact]
    public void Parse_MultipleModels_PreservesOrder()
    {
        var yaml = "models:\n" +
                   "  alpha:\n" +
                   "    cmd: \"a-cmd\"\n" +
                   "  beta:\n" +
                   "    cmd: \"b-cmd\"\n" +
                   "    ttl: 60\n";

        var result = LlamaSwapConfigParser.Parse(yaml);

        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result[0].Name);
        Assert.Equal("beta", result[1].Name);
        Assert.Equal(0, result[0].Ttl);
        Assert.Equal(60, result[1].Ttl);
    }

    [Fact]
    public void Parse_ConcurrentGroup_MarksResidentModels()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("embed", "llama-server -m e.gguf --port 9000", 0, 0, [], Concurrent: true),
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf --port 8080", 0, 300, [])]);

        var result = LlamaSwapConfigParser.Parse(yaml);

        var embed = result.Single(s => s.Name == "embed");
        var qwen = result.Single(s => s.Name == "qwen");
        Assert.True(embed.Concurrent);
        Assert.False(qwen.Concurrent);
    }

    [Fact]
    public void Parse_NoConcurrentGroup_AllNonConcurrent()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf", 0, 300, [])]);

        var result = LlamaSwapConfigParser.Parse(yaml);

        Assert.Single(result);
        Assert.False(result[0].Concurrent);
    }

    [Fact]
    public void Parse_QuotedKey_WithSpecialChars_Unquotes()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("my model", "cmd with \"quotes\"", 0, 0, [])]);

        var result = LlamaSwapConfigParser.Parse(yaml);

        var spec = Assert.Single(result);
        Assert.Equal("my model", spec.Name);
        Assert.Equal("cmd with \"quotes\"", spec.LaunchCommand);
    }

    [Fact]
    public void Parse_Comments_AreIgnored()
    {
        var yaml = "# top comment\n" +
                   "models:\n" +
                   "  # model comment\n" +
                   "  qwen:\n" +
                   "    cmd: \"llama-server -m q.gguf\"\n";

        var result = LlamaSwapConfigParser.Parse(yaml);

        var spec = Assert.Single(result);
        Assert.Equal("qwen", spec.Name);
    }

    [Fact]
    public void Parse_RoundTrip_GeneratedYaml_ProducesSameSpecs()
    {
        var originals = new[]
        {
            new LlamaSwapModelSpec("small", "llama-server -m s.gguf --ctx-size 2048", 2048, 300, []),
            new LlamaSwapModelSpec("embed", "llama-server -m e.gguf --port 9000", 0, 0, [], Concurrent: true),
            new LlamaSwapModelSpec("big", "llama-server -m b.gguf --ctx-size 8192", 8192, 600, ["-ngl 99"])
        };

        var yaml = LlamaSwapConfigGenerator.Generate(originals);
        var parsed = LlamaSwapConfigParser.Parse(yaml);

        Assert.Equal(originals.Length, parsed.Count);
        var byName = parsed.ToDictionary(p => p.Name);
        foreach (var orig in originals)
        {
            var p = byName[orig.Name];
            Assert.Equal(LlamaSwapConfigGenerator.BuildCommand(orig), p.LaunchCommand);
            Assert.Equal(orig.Ttl, p.Ttl);
            Assert.Equal(orig.Concurrent, p.Concurrent);
        }
    }

    [Fact]
    public void Parse_MissingTtl_DefaultsToZero()
    {
        var yaml = "models:\n  m:\n    cmd: \"x\"\n";
        var result = LlamaSwapConfigParser.Parse(yaml);
        Assert.Equal(0, result[0].Ttl);
    }

    [Fact]
    public void Parse_MissingCmd_DefaultsToEmpty()
    {
        var yaml = "models:\n  m:\n    ttl: 10\n";
        var result = LlamaSwapConfigParser.Parse(yaml);
        Assert.Equal("", result[0].LaunchCommand);
    }

    [Fact]
    public void Parse_CrlfLineEndings_AreHandled()
    {
        var yaml = "models:\r\n  qwen:\r\n    cmd: \"llama-server\"\r\n";
        var result = LlamaSwapConfigParser.Parse(yaml);
        Assert.Single(result);
        Assert.Equal("qwen", result[0].Name);
    }
}
