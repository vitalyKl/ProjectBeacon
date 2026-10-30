namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;

/// <summary>
/// An isolated detached worktree for one eval run. Eval pairs must start from the same pinned
/// revision and must not observe the other run's edits.
/// </summary>
internal sealed class EvalWorktree : IAsyncDisposable
{
    private readonly string _originalWorkingDirectory;
    private readonly string? _repositoryRoot;
    private readonly string? _worktreeRoot;
    private readonly ClientOpenCodeServe? _openCode;
    private bool _preserve;

    private EvalWorktree(
        string originalWorkingDirectory,
        string workingDirectory,
        string? repositoryRoot,
        string? worktreeRoot,
        ClientOpenCodeServe? openCode)
    {
        _originalWorkingDirectory = originalWorkingDirectory;
        WorkingDirectory = workingDirectory;
        _repositoryRoot = repositoryRoot;
        _worktreeRoot = worktreeRoot;
        _openCode = openCode;
    }

    public string WorkingDirectory { get; }

    /// <summary>Keep the detached worktree when an agent failed to stop, avoiding deletion under a live process.</summary>
    public void Preserve() => _preserve = true;

    public static async Task<(EvalWorktree? Workspace, string? Error)> CreateAsync(
        string workingDirectory,
        string? pinnedRevision,
        Guid evalRunId,
        ClientOpenCodeServe? openCode,
        CancellationToken ct)
    {
        var original = Path.GetFullPath(workingDirectory);
        if (string.IsNullOrWhiteSpace(pinnedRevision))
            return (new EvalWorktree(original, original, null, null, null), null);

        var rootResult = await RunGitAsync(original, ct, "rev-parse", "--show-toplevel");
        if (rootResult.ExitCode != 0 || rootResult.TimedOut || string.IsNullOrWhiteSpace(rootResult.StdOut))
            return (null, "Could not resolve the Git repository root for the pinned eval.");

        var repositoryRoot = Path.GetFullPath(rootResult.StdOut.Trim());
        // --show-prefix is computed by Git from the current worktree and remains correct when
        // the user reached the checkout through a symlink (e.g. /tmp on macOS/Linux).
        var prefixResult = await RunGitAsync(original, ct, "rev-parse", "--show-prefix");
        if (prefixResult.ExitCode != 0 || prefixResult.TimedOut)
            return (null, "Could not resolve the eval working directory relative to its Git root.");
        var relativeDirectory = prefixResult.StdOut.Trim();
        var relativePath = relativeDirectory.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var worktreesDirectory = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "eval-worktrees");
        var worktreeRoot = Path.Combine(worktreesDirectory, $"{evalRunId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreesDirectory);

        var add = await RunGitAsync(
            repositoryRoot,
            ct,
            "worktree",
            "add",
            "--detach",
            worktreeRoot,
            pinnedRevision.Trim());
        if (add.ExitCode != 0 || add.TimedOut)
        {
            try
            {
                if (Directory.Exists(worktreeRoot))
                    Directory.Delete(worktreeRoot, recursive: true);
            }
            catch { }

            var detail = string.IsNullOrWhiteSpace(add.StdErr) ? add.StdOut : add.StdErr;
            return (null, string.IsNullOrWhiteSpace(detail)
                ? "Could not create an isolated Git worktree for the eval."
                : $"Could not create an isolated Git worktree for the eval: {detail.Trim()}");
        }

        var targetDirectory = string.IsNullOrWhiteSpace(relativePath)
            ? worktreeRoot
            : Path.GetFullPath(Path.Combine(worktreeRoot, relativePath));
        var targetRelativePath = Path.GetRelativePath(worktreeRoot, targetDirectory);
        if (Path.IsPathRooted(targetRelativePath)
            || targetRelativePath == ".."
            || targetRelativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || targetRelativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
            return (null, "The resolved eval working directory escapes its isolated worktree.");
        }

        if (!Directory.Exists(targetDirectory))
        {
            await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
            return (null, "The pinned revision does not contain the eval working directory.");
        }

        return (new EvalWorktree(original, targetDirectory, repositoryRoot, worktreeRoot, openCode), null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worktreeRoot is null || _repositoryRoot is null || _preserve)
            return;

        // OpenCode can keep the eval directory as its process working directory. Move it back
        // before deleting the worktree (Windows does not allow deleting the active directory).
        if (_openCode is not null)
        {
            try { await _openCode.TickAsync(_originalWorkingDirectory, CancellationToken.None); }
            catch { /* The next daemon readiness check will retry starting OpenCode. */ }
        }

        await RemoveWorktreeAsync(_repositoryRoot, _worktreeRoot);
    }

    private static async Task RemoveWorktreeAsync(string repositoryRoot, string worktreeRoot)
    {
        var remove = await RunGitAsync(
            repositoryRoot,
            CancellationToken.None,
            "worktree",
            "remove",
            "--force",
            worktreeRoot);

        if (Directory.Exists(worktreeRoot))
        {
            try { Directory.Delete(worktreeRoot, recursive: true); }
            catch { /* Preserve the eval result; a stale path can be pruned by Git later. */ }
        }

        if (remove.ExitCode != 0)
            _ = await RunGitAsync(repositoryRoot, CancellationToken.None, "worktree", "prune");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunGitAsync(
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        return ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(45), ct);
    }
}
