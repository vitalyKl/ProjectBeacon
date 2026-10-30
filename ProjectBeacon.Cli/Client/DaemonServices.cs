namespace ProjectBeacon.Cli.Client;

using Application.Runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Process objects the daemon already owns. The container must not dispose them.
/// </summary>
internal sealed class DaemonDependencies
{
    public DaemonDependencies(
        HttpClient http,
        ClientLlamaSwap llama,
        ClientOpenCodeServe openCode,
        IAgentRuntime runtime,
        Func<WorkstationSettings> loadSettings,
        Func<IDaemonLlamaSync, IAgentRuntime>? runtimeFactory = null)
    {
        Http = http;
        Llama = llama;
        OpenCode = openCode;
        Runtime = runtime;
        LoadSettings = loadSettings;
        RuntimeFactory = runtimeFactory;
    }

    public HttpClient Http { get; }
    public ClientLlamaSwap Llama { get; }
    public ClientOpenCodeServe OpenCode { get; }
    public IAgentRuntime Runtime { get; }
    public Func<WorkstationSettings> LoadSettings { get; }
    public Func<IDaemonLlamaSync, IAgentRuntime>? RuntimeFactory { get; }
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
            deps.RuntimeFactory?.Invoke(sp.GetRequiredService<IDaemonLlamaSync>()) ?? deps.Runtime);
        services.AddSingleton<IDaemonOpenCodeGate, DaemonOpenCodeGate>();
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
