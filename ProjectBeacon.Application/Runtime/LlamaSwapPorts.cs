namespace ProjectBeacon.Infrastructure.LlamaSwap;

/// <summary>Name and state of one model loaded in llama-swap.</summary>
public record LoadedModelStatus(string Name, string State);

/// <summary>One GPU sample attached to a host-load reading.</summary>
public record GpuLoadDto(
    string? Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes);

/// <summary>CPU, memory, and optional GPU sample from a host.</summary>
public record HostLoadDto(
    double? CpuPercent,
    long? RamUsedBytes,
    long? RamTotalBytes,
    GpuLoadDto? Gpu,
    DateTimeOffset? SampledAt);

/// <summary>llama-swap availability, loaded models, and an optional host sample.</summary>
public record LlamaSwapStatusDto(
    bool Available,
    bool Healthy,
    string? LoadedModel,
    string? Memory,
    DateTime? LastSwap,
    string? Error,
    IReadOnlyList<LoadedModelStatus>? LoadedModels = null,
    HostLoadDto? Host = null,
    string? DeviceName = null);

/// <summary>Reads llama-swap status and asks it to reload or unload.</summary>
public interface ILlamaSwapProxy
{
    Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<bool> ReloadAsync(CancellationToken ct = default);
    Task<bool> UnloadAsync(CancellationToken ct = default);
}

/// <summary>Proxy used when the supervisor is not running. Status is unavailable and reload and unload return false.</summary>
public sealed class UnavailableLlamaSwapProxy : ILlamaSwapProxy
{
    public Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap supervisor is not running."));

    public Task<bool> ReloadAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> UnloadAsync(CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>One model entry used to build a llama-swap catalog.</summary>
public sealed record LlamaSwapModelBinding(
    string Name,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    IReadOnlyList<string> ExtraFlags,
    bool Concurrent = false);

/// <summary>Listening port plus builders for a model launch spec and catalog YAML.</summary>
public interface ILlamaSwapCatalog
{
    int Port { get; }

    string BuildLaunchSpec(string name, string launchCommand, int contextSize, int ttl, IReadOnlyList<string> extraFlags);

    string GenerateYaml(IReadOnlyList<LlamaSwapModelBinding> models);
}
