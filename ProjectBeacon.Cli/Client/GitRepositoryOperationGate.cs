namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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

    /// <summary>Attempts to acquire the repository operation gate without waiting indefinitely.</summary>
    public static async ValueTask<IDisposable?> TryAcquireAsync(
        string repositoryRoot, TimeSpan timeout, CancellationToken ct)
    {
        var state = GetState(repositoryRoot);
        return await state.Gate.WaitAsync(timeout, ct)
            ? new GateLease(state.Gate)
            : null;
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
        if (root is null)
            return fullPath;

        // On Unix, realpath resolves both symlinks and filesystem aliases such as the
        // macOS /var -> /private/var mapping. Resolve the full existing path in one
        // operation so a lease registered through an alias matches Git's physical path.
        if (!OperatingSystem.IsWindows())
        {
            // realpath requires the complete path to exist. For a not-yet-created
            // checkout (for example InitProject's target directory), keep the normalized
            // lexical path instead of falling through to FileSystemInfo.ResolveLinkTarget.
            var physicalPath = TryResolvePhysicalPath(fullPath);
            return TrimRoot(physicalPath ?? fullPath);
        }

        // Windows junctions and symbolic links are resolved component by component.
        var current = root;
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            // ResolveLinkTarget requires an existing filesystem entry. AutoSync and
            // InitProject also lock paths that may not have been created yet, so retain
            // the lexical suffix until an existing component is reached.
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = Path.GetFullPath(target.FullName);
        }

        return TrimRoot(current);
    }

    private static string TrimRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(path, root, comparison)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryResolvePhysicalPath(string path)
    {
        IntPtr resolved = IntPtr.Zero;
        try
        {
            resolved = RealPath(path, IntPtr.Zero);
            return resolved == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(resolved);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (resolved != IntPtr.Zero)
                Free(resolved);
        }
    }

    [DllImport("libc", EntryPoint = "realpath", ExactSpelling = true)]
    private static extern IntPtr RealPath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free", ExactSpelling = true)]
    private static extern void Free(IntPtr pointer);

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
