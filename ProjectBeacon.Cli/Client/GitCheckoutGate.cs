namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;

/// <summary>
/// Serializes mutations of a real project checkout. Agent task/snapshot worktrees do not acquire this gate.
/// </summary>
internal static class GitCheckoutGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<IDisposable> AcquireAsync(string repositoryRoot, CancellationToken ct)
    {
        var gate = GetGate(repositoryRoot);
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    public static async ValueTask<IDisposable?> TryAcquireAsync(
        string repositoryRoot, TimeSpan timeout, CancellationToken ct)
    {
        var gate = GetGate(repositoryRoot);
        return await gate.WaitAsync(timeout, ct) ? new Lease(gate) : null;
    }

    private static SemaphoreSlim GetGate(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException("A repository root is required.", nameof(repositoryRoot));
        return Gates.GetOrAdd(GitRepositoryOperationGate.NormalizeRoot(repositoryRoot), static _ => new SemaphoreSlim(1, 1));
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
