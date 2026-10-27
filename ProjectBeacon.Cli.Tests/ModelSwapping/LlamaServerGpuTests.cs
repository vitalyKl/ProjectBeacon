namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client.ModelSwapping;
using ProjectBeacon.Infrastructure.LlamaSwap;

[Trait("Lane", "Gpu")]
public sealed class LlamaServerGpuTests : IDisposable
{
    public const string ModelEnvVar = "BEACON_TEST_GPU_MODEL";
    private const string DefaultModelFile = "test.gguf";
    private readonly List<LlamaServerBackend> _backends = [];

    /// <summary>
    /// Resolves the GPU-lane GGUF: <see cref="ModelEnvVar"/> wins, falling back to <c>test.gguf</c> in
    /// the test host's working directory. Null when neither exists — the model is deliberately
    /// never committed; the GPU lane targets the self-hosted runner (ci-gpu.yml dispatch),
    /// where it is staged once.
    /// </summary>
    internal static string? ResolveModelPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ModelEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var full = Path.GetFullPath(fromEnv);
            return File.Exists(full) ? full : null;
        }
        var def = Path.GetFullPath(DefaultModelFile);
        return File.Exists(def) ? def : null;
    }

    private static string ModelMissingMessage()
    {
        return "GPU model not staged: " + ModelEnvVar + " is unset and " + DefaultModelFile +
            " is missing from the test working directory. Stage a small valid GGUF on the GPU runner " +
            "(or set " + ModelEnvVar + ") and run with -p:TestLane=gpu; see " +
            "docs/development/test-environment.md. The GPU lane is excluded from default runs " +
            "(Directory.Build.targets).";
    }

    [Fact]
    public async Task StartAsync_RealBinary_ReachesReady()
    {
        var model = ResolveModelPath();
        if (model is null)
        {
            // On GitHub Actions (the only place the GPU lane runs via ci-gpu.yml) a missing model
            // fails the run, so the dispatch lane never passes vacuously. Locally/VS it is a
            // reported skip: VS Test Explorer ignores the MSBuild lane filter, and the model is
            // never committed.
            if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
            {
                Assert.Fail(ModelMissingMessage());
            }
            Assert.Skip(ModelMissingMessage() + " Locally this is expected.");
        }

        var backend = new LlamaServerBackend
        {
            Name = "gpu-lane",
            Port = 18181,
            SkipRealProcess = false
        };
        _backends.Add(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await backend.StartAsync(new LlamaSwapModelSpec("gpu-lane", $"llama-server -m \"{model}\"", 512, 0, []), cts.Token);
        Assert.Equal(BackendState.Ready, backend.State);
    }

    public void Dispose()
    {
        foreach (var backend in _backends)
            backend.Dispose();
    }
}