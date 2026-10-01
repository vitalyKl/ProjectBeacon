namespace ProjectBeacon.Cli.Client.ModelSwapping;

using ProjectBeacon.Infrastructure.LlamaSwap;

/// <summary>
/// One locally-running inference model. The client-side swapper owns these directly instead of
/// delegating to the external <c>llama-swap</c> binary. Implementations read a pre-parsed
/// <see cref="LlamaSwapModelSpec"/>, never the raw YAML.
/// </summary>
public interface IModelBackend
{
    string Name { get; }
    int Endpoint { get; }
    BackendState State { get; }
    string? Error { get; }

    long WorkingSetMb { get; }

    long ActualVramMb { get; }

    long EstimatedVramMb { get; }

    long VramFootprintMb { get; }

    Task StartAsync(LlamaSwapModelSpec spec, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<bool> HealthCheckAsync(CancellationToken ct);
}
