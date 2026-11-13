namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;

/// <summary>Serializes operations that must not overlap on a real project repository.</summary>
internal static class GitRepositoryOperationGate
{
    private sealed class RepositoryState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object SessionSync { get; } = new();
        public int ActiveSessions;
        public Dictionary<string, int> ActiveWorktrees { get; } = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public TaskCompletionSource<bool>? SessionsIdle;
    }

    private static readonly ConcurrentDictionary<string, RepositoryState> States =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<IDisposable> AcquireAsync(string repositoryRoot, CancellationToken ct)
    {
        var state = GetState(repositoryRoot);
        await state.Gate.WaitAsync(ct);
        return new GateLease(state.Gate);
    }

    public static IDisposable RegisterSession(string repositoryRoot) =>
        RegisterSession(repositoryRoot, repositoryRoot);

    public static IDisposable RegisterSession(string repositoryRoot, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot))
            throw new ArgumentException("A worktree root is required.", nameof(worktreeRoot));

        var state = GetState(repositoryRoot);
        var normalizedWorktree = NormalizeRoot(worktreeRoot);
        lock (state.SessionSync)
        {
            if (state.ActiveSessions == 0)
                state.SessionsIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.ActiveSessions++;
            state.ActiveWorktrees.TryGetValue(normalizedWorktree, out var count);
            state.ActiveWorktrees[normalizedWorktree] = count + 1;
        }
        return new SessionLease(state, normalizedWorktree);
    }

    /// <summary>
    /// True only when the supplied isolated worktree owns the sole active session lease
    /// for this repository. This permits that session to fast-forward its own clean baseline
    /// without changing another agent's checkout underneath it.
    /// </summary>
    public static bool IsOnlyActiveSession(string repositoryRoot, string worktreeRoot)
    {
        var state = GetState(repositoryRoot);
        var normalizedWorktree = NormalizeRoot(worktreeRoot);
        lock (state.SessionSync)
        {
            return state.ActiveSessions == 1
                && state.ActiveWorktrees.Count == 1
                && state.ActiveWorktrees.TryGetValue(normalizedWorktree, out var count)
                && count == 1;
        }
    }

    /// <summary>Waits for all sessions registered before and during this wait to release their leases.</summary>
    public static Task WaitForNoSessionsAsync(string repositoryRoot, CancellationToken ct)
    {
        var state = GetState(repositoryRoot);
        Task idle;
        lock (state.SessionSync)
        {
            if (state.ActiveSessions == 0)
                return Task.CompletedTask;
            idle = state.SessionsIdle!.Task;
        }
        return idle.WaitAsync(ct);
    }

    public static bool HasActiveSessions(string repositoryRoot)
    {
        var state = GetState(repositoryRoot);
        lock (state.SessionSync)
            return state.ActiveSessions > 0;
    }

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

    private sealed class SessionLease(RepositoryState state, string worktreeRoot) : IDisposable
    {
        private RepositoryState? _state = state;
        private readonly string _worktreeRoot = worktreeRoot;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _state, null);
            if (current is null)
                return;

            TaskCompletionSource<bool>? idle = null;
            lock (current.SessionSync)
            {
                if (current.ActiveSessions <= 0)
                    throw new InvalidOperationException("Repository session lease count is invalid.");
                current.ActiveSessions--;
                if (!current.ActiveWorktrees.TryGetValue(_worktreeRoot, out var worktreeCount) || worktreeCount <= 0)
                    throw new InvalidOperationException("Repository worktree session lease count is invalid.");
                if (worktreeCount == 1)
                    current.ActiveWorktrees.Remove(_worktreeRoot);
                else
                    current.ActiveWorktrees[_worktreeRoot] = worktreeCount - 1;
                if (current.ActiveSessions == 0)
                {
                    if (current.ActiveWorktrees.Count != 0)
                        throw new InvalidOperationException("Repository worktree leases remain after the last session.");
                    idle = current.SessionsIdle;
                    current.SessionsIdle = null;
                }
            }
            idle?.TrySetResult(true);
        }
    }
}
