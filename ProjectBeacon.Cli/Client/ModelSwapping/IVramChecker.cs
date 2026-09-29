namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Reports free VRAM in megabytes. Used before starting a model to avoid OOM.
/// </summary>
public interface IVramChecker
{
    /// <returns>Free VRAM in MiB, or -1 if the GPU / driver is unavailable.</returns>
    Task<long> GetFreeVramMbAsync(CancellationToken ct);
}

public sealed class NoopVramChecker : IVramChecker
{
    public Task<long> GetFreeVramMbAsync(CancellationToken ct) => Task.FromResult(-1L);
}
