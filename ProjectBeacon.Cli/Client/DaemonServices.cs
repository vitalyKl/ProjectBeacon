namespace ProjectBeacon.Cli.Client;

using Application.Runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Process objects owned by the workstation daemon and its runtime factory.</summary>
internal sealed class DaemonDependencies
{
    public DaemonDependencies(
        HttpClient http,
        ClientLlamaSwap llama,
        Func<WorkstationSettings> loadSettings,
        Func<IDaemonLlamaSync, IAgentRuntime> runtimeFactory)
    {
        Http = http;
        Llama = llama;
        LoadSettings = loadSettings;
        RuntimeFactory = runtimeFactory;
    }

    public HttpClient Http { get; }
    public ClientLlamaSwap Llama { get; }
    public Func<WorkstationSettings> LoadSettings { get; }
    public Func<IDaemonLlamaSync, IAgentRuntime> RuntimeFactory { get; }
}

internal static class DaemonServices
{
    public static ServiceProvider Build(DaemonDependencies deps, DaemonTiming timing, IDaemonRuntimeState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(deps);
        services.AddSingleton(timing);
        services.AddSingleton(state);
        services.AddSingleton<IDaemonLlamaSync, DaemonLlamaSync>();
        services.AddSingleton<IAgentRuntime>(sp =>
            deps.RuntimeFactory(sp.GetRequiredService<IDaemonLlamaSync>()));
        services.AddSingleton<IDaemonAgentRuntimeGate, DaemonAgentRuntimeGate>();
        services.AddSingleton<GitSyncService>();
        services.AddSingleton<IDaemonChatSession, DaemonChatSession>();
        services.AddSingleton<IDaemonCommandExecutor, DaemonCommandExecutor>();
        services.AddSingleton<IDaemonLoop, DaemonHeartbeatLoop>();
        services.AddSingleton<IDaemonLoop, DaemonCommandLoop>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
