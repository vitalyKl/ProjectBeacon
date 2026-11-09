namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;

/// <summary>Serializes operations that must not overlap on a real project repository.</summary>
internal static class GitRepositoryOperationGate
{
    private sealed class RepositoryState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ActiveSessions;
    }

    private static readonly ConcurrentDictionary<string, RepositoryState> States =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<IDisposable> AcquireAsync(string repositoryRoot, CancellationToken ct)
    {
        var state = GetState(repositoryRoot);
        await state.Gate.WaitAsync(ct);
        return new GateLease(state.Gate);
    }

    public static IDisposable RegisterSession(string repositoryRoot)
    {
        var state = GetState(repositoryRoot);
        Interlocked.Increment(ref state.ActiveSessions);
        return new SessionLease(state);
    }

    public static bool HasActiveSessions(string repositoryRoot) =>
        Volatile.Read(ref GetState(repositoryRoot).ActiveSessions) > 0;

    public static string NormalizeRoot(string repositoryRoot)
    {
        var fullPath = Path.GetFullPath(repositoryRoot);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static RepositoryState GetState(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException("A repository root is required.", nameof(repositoryRoot));
        return States.GetOrAdd(NormalizeRoot(repositoryRoot), static _ => new RepositoryState());
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class SessionLease(RepositoryState state) : IDisposable
    {
        private RepositoryState? _state = state;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _state, null);
            if (current is not null)
                Interlocked.Decrement(ref current.ActiveSessions);
        }
    }
}
