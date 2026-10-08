namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Free VRAM in MiB. <see cref="VramReading.FreeMb"/> &lt; 0 with <see cref="VramReading.QueryFailed"/> false
/// means no GPU tool. <see cref="VramReading.QueryFailed"/> means the probe ran and failed.
/// </summary>
public readonly record struct VramReading(long FreeMb, bool QueryFailed);
/// <summary>
/// Reads free GPU memory in MiB. See VramReading for how a missing tool differs from a failed query.
/// </summary>
public interface IVramChecker
{
    Task<VramReading> ReadFreeAsync(CancellationToken ct);
}
/// <summary>
/// Reports no GPU tool: free memory -1 and QueryFailed false.
/// </summary>
public sealed class NoopVramChecker : IVramChecker
{
    public Task<VramReading> ReadFreeAsync(CancellationToken ct) =>
        Task.FromResult(new VramReading(-1, false));
}
