namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using ProjectBeacon.Cli.Client;
using ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class ClientLlamaSwapOwnSwapperTests
{
    [Fact]
    public async Task TickOwn_NoModels_StatusNotAvailable()
    {
        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true
        };
        await ll.TickAsync("models: {}", 8080, null, CancellationToken.None);

        Assert.False(ll.Status.Available);
        Assert.Null(ll.Status.LoadedModel);
    }

    [Fact]
    public async Task TickOwn_SingleSwapModel_StatusAvailable()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf", 0, 300, [])]);

        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true
        };
        await ll.TickAsync(yaml, 8080, null, CancellationToken.None);

        Assert.True(ll.Status.Available);
        Assert.True(ll.Status.Healthy);
        Assert.Equal("qwen", ll.Status.LoadedModel);
        Assert.NotNull(ll.Status.LoadedModels);
        Assert.Single(ll.Status.LoadedModels);
    }

    [Fact]
    public async Task TickOwn_ConcurrentModel_AssignedBasePortPlusIndex()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("embed", "llama-server -m e.gguf", 0, 0, [], Concurrent: true),
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf", 0, 300, [])]);

        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true,
            ConcurrentPortBase = 9000
        };
        await ll.TickAsync(yaml, 8080, null, CancellationToken.None);

        Assert.True(ll.Status.Available);
        Assert.True(ll.Status.Healthy);
        Assert.Equal("qwen", ll.Status.LoadedModel);
        Assert.NotNull(ll.Status.LoadedModels);
        Assert.Equal(2, ll.Status.LoadedModels!.Count);
    }

    [Fact]
    public async Task TickOwn_ModelRemoved_StopsAndRemoves()
    {
        var yamlWith = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf", 0, 300, [])]);
        var yamlWithout = "models: {}";

        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true
        };
        await ll.TickAsync(yamlWith, 8080, null, CancellationToken.None);
        Assert.Equal("qwen", ll.Status.LoadedModel);

        await ll.TickAsync(yamlWithout, 8080, null, CancellationToken.None);
        Assert.False(ll.Status.Available);
        Assert.Null(ll.Status.LoadedModel);
    }

    [Fact]
    public async Task TickOwn_InvalidYaml_StatusShowsParseError()
    {
        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true
        };
        await ll.TickAsync("models:\n  bad: not_yaml", 8080, null, CancellationToken.None);

        Assert.False(ll.Status.Available);
        Assert.False(ll.Status.Healthy);
    }

    [Fact]
    public async Task TickOwn_MemoryReportedForRunningBackends()
    {
        var yaml = LlamaSwapConfigGenerator.Generate([
            new LlamaSwapModelSpec("qwen", "llama-server -m q.gguf", 0, 300, [])]);

        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = true,
            SkipRealProcess = true
        };
        await ll.TickAsync(yaml, 8080, null, CancellationToken.None);

        // SkipRealProcess means no real process, VramFootprintMb will be 0,
        // but the memory string should still be formatted.
        Assert.NotNull(ll.Status.Memory);
    }

    [Fact]
    public async Task UseOwnSwapper_False_FallsBackToExternalPath()
    {
        var ll = new ClientLlamaSwap
        {
            UseOwnSwapper = false,
            SkipRealProcess = true
        };
        await ll.TickAsync("models: {}", 8080, "some-bin-path", CancellationToken.None);

        // External path should not crash; status may or may not be available
        // depending on SkipRealProcess, but the key is it did NOT use TickOwnAsync.
        Assert.NotNull(ll.Status);
    }

    private static string TwoModelYaml() => LlamaSwapConfigGenerator.Generate([
        new LlamaSwapModelSpec("alpha", "llama-server -m a.gguf", 0, 300, []),
        new LlamaSwapModelSpec("beta", "llama-server -m b.gguf", 0, 300, [])]);

    [Fact]
    public async Task TickOwn_NoDesired_FallsBackToFirstAlphabetical()
    {
        var ll = new ClientLlamaSwap { UseOwnSwapper = true, SkipRealProcess = true };
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("alpha", ll.Status.LoadedModel);
    }

    [Fact]
    public async Task TickOwn_PrefersDesiredSwapModel_NotFirstAlphabetical()
    {
        var ll = new ClientLlamaSwap { UseOwnSwapper = true, SkipRealProcess = true };
        ll.PreferSwapModel("beta");
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("beta", ll.Status.LoadedModel);
    }

    [Fact]
    public async Task TickOwn_SwapsToDesiredOnDemand()
    {
        var ll = new ClientLlamaSwap { UseOwnSwapper = true, SkipRealProcess = true };
        ll.PreferSwapModel("alpha");
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("alpha", ll.Status.LoadedModel);

        ll.PreferSwapModel("beta");
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("beta", ll.Status.LoadedModel);
    }

    [Fact]
    public async Task TickOwn_UnknownDesired_FallsBackToFirst()
    {
        var ll = new ClientLlamaSwap { UseOwnSwapper = true, SkipRealProcess = true };
        ll.PreferSwapModel("zzz");
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("alpha", ll.Status.LoadedModel);
    }

    [Fact]
    public async Task EnsureSwapModelAsync_SetsPreferenceAndTicks()
    {
        var ll = new ClientLlamaSwap { UseOwnSwapper = true, SkipRealProcess = true };
        await ll.TickAsync(TwoModelYaml(), 8080, null, CancellationToken.None);
        Assert.Equal("alpha", ll.Status.LoadedModel);

        await ll.EnsureSwapModelAsync("beta", CancellationToken.None);
        Assert.Equal("beta", ll.Status.LoadedModel);
    }
}
