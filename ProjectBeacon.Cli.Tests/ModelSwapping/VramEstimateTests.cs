namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class VramEstimateTests
{
    [Fact]
    public void FromSpec_UsesGgufBytesNotWorkingSet()
    {
        var path = Path.Combine(Path.GetTempPath(), "beacon-vram-" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            File.WriteAllBytes(path, new byte[3 * 1024 * 1024]);
            var spec = new LlamaSwapModelSpec("qwen", $"llama-server -m \"{path}\"", 4096, 300, []);
            Assert.Equal(3, VramEstimate.FromSpec(spec));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void FromSpec_ContextStandInWhenFileMissing()
    {
        var spec = new LlamaSwapModelSpec("qwen", "llama-server -m missing.gguf", 4096, 300, []);
        Assert.Equal(4, VramEstimate.FromSpec(spec));
    }

    [Fact]
    public void ParseProcessMb_SumsMatchingPid()
    {
        var text = "12, 400\n99, 10\n12, 112\n";
        Assert.Equal(512, NvidiaSmiVramChecker.ParseProcessMb(text, 12));
        Assert.Equal(0, NvidiaSmiVramChecker.ParseProcessMb(text, 7));
    }
}
