namespace ProjectBeacon.Infrastructure.LlamaSwap;

public record LoadedModelStatus(string Name, string State);

public record GpuLoadDto(
    string? Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes);

public record HostLoadDto(
    double? CpuPercent,
    long? RamUsedBytes,
    long? RamTotalBytes,
    GpuLoadDto? Gpu,
    DateTimeOffset? SampledAt);

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

public interface ILlamaSwapProxy
{
    Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<bool> ReloadAsync(CancellationToken ct = default);
    Task<bool> UnloadAsync(CancellationToken ct = default);
}

public sealed class UnavailableLlamaSwapProxy : ILlamaSwapProxy
{
    public Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap supervisor is not running."));

    public Task<bool> ReloadAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> UnloadAsync(CancellationToken ct = default) => Task.FromResult(false);
}
