namespace ProjectBeacon.Infrastructure.LlamaSwap;

public sealed class LlamaSwapCatalog : ILlamaSwapCatalog
{
    public LlamaSwapCatalog(int port = 8080) => Port = port;

    public int Port { get; }

    public string BuildLaunchSpec(string name, string launchCommand, int contextSize, int ttl, IReadOnlyList<string> extraFlags)
        => LlamaSwapConfigGenerator.BuildCommand(new LlamaSwapModelSpec(name, launchCommand, contextSize, ttl, extraFlags));

    public string GenerateYaml(IReadOnlyList<LlamaSwapModelBinding> models)
        => LlamaSwapConfigGenerator.Generate(models
            .Select(m => new LlamaSwapModelSpec(m.Name, m.LaunchCommand, m.ContextSize, m.Ttl, m.ExtraFlags, m.Concurrent))
            .ToList());
}
