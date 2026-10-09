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
        Func<WorkstationSettings> loadSettings)
    {
        Http = http;
        Llama = llama;
        OpenCode = openCode;
        Runtime = runtime;
        LoadSettings = loadSettings;
    }

    public HttpClient Http { get; }
    public ClientLlamaSwap Llama { get; }
    public ClientOpenCodeServe OpenCode { get; }
    public IAgentRuntime Runtime { get; }
    public Func<WorkstationSettings> LoadSettings { get; }
}

internal static class DaemonServices
{
    public static ServiceProvider Build(DaemonDependencies deps, DaemonTiming timing, IDaemonRuntimeState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(deps);
        services.AddSingleton(timing);
        services.AddSingleton(state);
        services.AddSingleton<IDaemonOpenCodeGate, DaemonOpenCodeGate>();
        services.AddSingleton<IDaemonLlamaSync, DaemonLlamaSync>();
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
