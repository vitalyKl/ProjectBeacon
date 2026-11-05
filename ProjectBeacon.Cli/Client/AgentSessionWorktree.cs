namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text;
using ProjectBeacon.Cli.Client.AgentHarness;

/// <summary>
/// A disposable worktree for one agent execution. A temporary snapshot commit captures the
/// user's current index, tracked edits and untracked files without changing the user's index.
/// Only the agent's delta is applied back after a successful turn.
/// </summary>
internal sealed class AgentSessionWorktree : IAsyncDisposable
{
    private readonly string _repositoryRoot;
    private readonly string _originalWorkingDirectory;
    private readonly string _worktreeRoot;
    private string _snapshotCommit;
    private bool _preserve;

    private AgentSessionWorktree(
        string repositoryRoot,
        string originalWorkingDirectory,
        string workingDirectory,
        string worktreeRoot,
        string snapshotCommit)
    {
        _repositoryRoot = repositoryRoot;
        _originalWorkingDirectory = originalWorkingDirectory;
        WorkingDirectory = workingDirectory;
        _worktreeRoot = worktreeRoot;
        _snapshotCommit = snapshotCommit;
    }

    public string WorkingDirectory { get; }
    public string ProjectRoot => _originalWorkingDirectory;
    public string RecoveryPath => _worktreeRoot;

    public void Preserve() => _preserve = true;

    public static async Task<(AgentSessionWorktree? Workspace, string? Error)> CreateAsync(
        string workingDirectory,
        Guid sessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return (null, "The project directory does not exist.");

        var original = Path.GetFullPath(workingDirectory);
        var rootResult = await RunGitAsync(original, ct, "rev-parse", "--show-toplevel");
        if (rootResult.ExitCode != 0 || rootResult.TimedOut || string.IsNullOrWhiteSpace(rootResult.StdOut))
            return (null, "Agent command execution requires a Git repository so the session can use an isolated worktree.");

        var repositoryRoot = Path.GetFullPath(rootResult.StdOut.Trim());
        var hasFilters = await RunGitAsync(repositoryRoot, ct, "config", "--get-regexp", "^filter\\.");
        if (hasFilters.ExitCode == 0 && !string.IsNullOrWhiteSpace(hasFilters.StdOut))
            return (null, "This repository has configured Git content filters. Isolated worktree creation is disabled because those filters can execute external programs.");

        if (await HasExternalGitAttributesAsync(repositoryRoot, ct))
            return (null, "This repository declares Git content filters or working-tree encodings. Isolated worktree creation is disabled to avoid executing repository-provided filters.");

        var prefixResult = await RunGitAsync(original, ct, "rev-parse", "--show-prefix");
        if (prefixResult.ExitCode != 0 || prefixResult.TimedOut)
            return (null, "Could not resolve the project directory relative to its Git repository.");

        var headResult = await RunGitAsync(repositoryRoot, ct, "rev-parse", "--verify", "HEAD");
        if (headResult.ExitCode != 0 || headResult.TimedOut || string.IsNullOrWhiteSpace(headResult.StdOut))
            return (null, "The Git repository needs an initial commit before an isolated agent worktree can be created.");
        var head = headResult.StdOut.Trim();

        var worktreeParent = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "agent-worktrees");
        var worktreeRoot = Path.Combine(worktreeParent, $"{sessionId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktreeParent);
        var indexPath = worktreeRoot + ".snapshot.index";

        try
        {
            // Populate an alternate index from HEAD, then add the current working copy to it.
            // This snapshots user edits without running git add against their real index.
            var readTree = await RunGitAsync(repositoryRoot, ct, ["read-tree", head], indexPath);
            if (readTree.ExitCode != 0 || readTree.TimedOut)
                return await FailCreateAsync(repositoryRoot, worktreeRoot, readTree, "Could not prepare an isolated Git index.");

            var add = await RunGitAsync(repositoryRoot, ct, ["add", "-A", "--", "."], indexPath);
            if (add.ExitCode != 0 || add.TimedOut)
                return await FailCreateAsync(repositoryRoot, worktreeRoot, add, "Could not snapshot the project's current files.");

            var tree = await RunGitAsync(repositoryRoot, ct, ["write-tree"], indexPath);
            if (tree.ExitCode != 0 || tree.TimedOut || string.IsNullOrWhiteSpace(tree.StdOut))
                return await FailCreateAsync(repositoryRoot, worktreeRoot, tree, "Could not create the isolated worktree snapshot.");

            var snapshotEntries = await RunGitAsync(repositoryRoot, ct, "ls-tree", "-r", tree.StdOut.Trim());
            if (snapshotEntries.ExitCode != 0 || snapshotEntries.TimedOut)
                return await FailCreateAsync(repositoryRoot, worktreeRoot, snapshotEntries, "Could not inspect the isolated worktree snapshot.");
            if (snapshotEntries.StdOut.Split('\n').Any(line => line.StartsWith("120000 blob ", StringComparison.Ordinal)))
                return await FailCreateAsync(repositoryRoot, worktreeRoot, snapshotEntries, "The project contains symbolic links. Isolated command execution refuses to follow paths that can escape the worktree.");

            var snapshot = await RunGitAsync(
                repositoryRoot,
                ct,
                [
                    "-c", "user.name=ProjectBeacon",
                    "-c", "user.email=projectbeacon@localhost",
                    "-c", "commit.gpgsign=false",
                    "commit-tree", tree.StdOut.Trim(),
                    "-p", head,
                    "-m", $"ProjectBeacon session snapshot {sessionId:N}"
                ]);
            if (snapshot.ExitCode != 0 || snapshot.TimedOut || string.IsNullOrWhiteSpace(snapshot.StdOut))
                return await FailCreateAsync(repositoryRoot, worktreeRoot, snapshot, "Could not finalize the isolated worktree snapshot.");

            var snapshotCommit = snapshot.StdOut.Trim();
            var addWorktree = await RunGitAsync(repositoryRoot, ct, "worktree", "add", "--detach", worktreeRoot, snapshotCommit);
            if (addWorktree.ExitCode != 0 || addWorktree.TimedOut)
                return await FailCreateAsync(repositoryRoot, worktreeRoot, addWorktree, "Could not create the isolated Git worktree.");

            var relativeDirectory = prefixResult.StdOut.Trim()
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var targetDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? worktreeRoot
                : Path.GetFullPath(Path.Combine(worktreeRoot, relativeDirectory));
            var relativeTarget = Path.GetRelativePath(worktreeRoot, targetDirectory);
            if (Path.IsPathRooted(relativeTarget)
                || relativeTarget == ".."
                || relativeTarget.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativeTarget.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || !Directory.Exists(targetDirectory))
            {
                try { File.Delete(indexPath); } catch { }
                await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
                return (null, "The isolated project directory is missing or escapes the worktree.");
            }

            try { File.Delete(indexPath); } catch { }
            return (new AgentSessionWorktree(repositoryRoot, original, targetDirectory, worktreeRoot, snapshotCommit), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { File.Delete(indexPath); } catch { }
            await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try { File.Delete(indexPath); } catch { }
            await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
            return (null, $"Could not create an isolated Git worktree ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Applies only the changes made by the agent, leaving the user's staged/unstaged snapshot
    /// intact. A conflict preserves the worktree and its commit for manual recovery.
    /// </summary>
    public async Task<(bool Success, string? Error)> ApplyChangesAsync(CancellationToken ct)
    {
        var status = await RunGitAsync(WorkingDirectory, ct, "status", "--porcelain", "--untracked-files=all");
        if (status.ExitCode != 0 || status.TimedOut)
            return (false, "Could not inspect the isolated worktree changes.");

        if (!string.IsNullOrWhiteSpace(status.StdOut))
        {
            var stage = await RunGitAsync(WorkingDirectory, ct, "add", "-A", "--", ".");
            if (stage.ExitCode != 0 || stage.TimedOut)
                return (false, "Could not stage the agent's worktree changes.");

            var summary = await RunGitAsync(WorkingDirectory, ct, "diff", "--cached", "--summary", _snapshotCommit);
            if (summary.ExitCode != 0 || summary.TimedOut)
                return (false, "Could not inspect the agent's worktree diff.");
            if (summary.StdOut.Contains("120000", StringComparison.Ordinal)
                || summary.StdOut.Contains("symbolic link", StringComparison.OrdinalIgnoreCase))
                return (false, $"The agent created or changed a symbolic link. The worktree was preserved at {_worktreeRoot}.");

            var commit = await RunGitAsync(
                WorkingDirectory,
                ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon@localhost",
                "-c", "commit.gpgsign=false",
                "commit", "-m", "ProjectBeacon: apply verified agent session");
            if (commit.ExitCode != 0 || commit.TimedOut)
                return (false, $"Could not commit the agent worktree changes. The worktree was preserved at {_worktreeRoot}.");
        }

        var newHead = await RunGitAsync(WorkingDirectory, ct, "rev-parse", "HEAD");
        if (newHead.ExitCode != 0 || newHead.TimedOut || string.IsNullOrWhiteSpace(newHead.StdOut))
            return (false, $"Could not identify the agent worktree commit. The worktree was preserved at {_worktreeRoot}.");

        var nextSnapshot = newHead.StdOut.Trim();
        if (string.Equals(nextSnapshot, _snapshotCommit, StringComparison.Ordinal))
            return (true, null);

        // Diff from the last successfully applied baseline, not just the current index. This
        // lets a later chat turn retry a patch after a previous turn hit a conflict.
        var patch = await RunGitAsync(_repositoryRoot, ct, "diff", "--binary", "--no-ext-diff", "--no-textconv", _snapshotCommit, nextSnapshot);
        if (patch.ExitCode != 0 || patch.TimedOut)
            return (false, $"Could not create a patch for the agent changes. The worktree was preserved at {_worktreeRoot}.");
        if (string.IsNullOrEmpty(patch.StdOut))
        {
            _snapshotCommit = nextSnapshot;
            _preserve = false;
            return (true, null);
        }

        var patchPath = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "agent-worktree-patches", Guid.NewGuid().ToString("N") + ".patch");
        Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
        await File.WriteAllTextAsync(patchPath, patch.StdOut, new UTF8Encoding(false), ct);
        try
        {
            var apply = await RunGitAsync(_repositoryRoot, ct, "apply", "--binary", "--whitespace=nowarn", patchPath);
            if (apply.ExitCode != 0 || apply.TimedOut)
                return (false, $"The agent changes conflict with the current project files. The worktree was preserved at {_worktreeRoot}; patch: {patchPath}.");

            _snapshotCommit = nextSnapshot;
            _preserve = false;
            return (true, null);
        }
        finally
        {
            if (string.Equals(_snapshotCommit, nextSnapshot, StringComparison.Ordinal))
            {
                try { File.Delete(patchPath); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_preserve)
            return;
        await RemoveWorktreeAsync(_repositoryRoot, _worktreeRoot);
    }

    private static async Task<(AgentSessionWorktree? Workspace, string? Error)> FailCreateAsync(
        string repositoryRoot,
        string worktreeRoot,
        (int ExitCode, string StdOut, string StdErr, bool TimedOut) result,
        string message)
    {
        try { File.Delete(worktreeRoot + ".snapshot.index"); } catch { }
        await RemoveWorktreeAsync(repositoryRoot, worktreeRoot);
        var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        return (null, string.IsNullOrWhiteSpace(detail) ? message : message + " " + Clip(detail));
    }

    private static string Clip(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 400 ? normalized : normalized[..400] + "…";
    }

    private static async Task<bool> HasExternalGitAttributesAsync(string root, CancellationToken ct)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth < 5 && name is not (".git" or "node_modules" or "bin" or "obj" or "target" or "vendor"))
                        pending.Enqueue((entry, depth + 1));
                    continue;
                }
                if (name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase))
                {
                    string contents;
                    try { contents = await File.ReadAllTextAsync(entry, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                        contents,
                        @"(?im)(?:^|\s)(?:filter|working-tree-encoding)\s*="))
                        return true;
                }
            }
        }
        return false;
    }

    private static async Task RemoveWorktreeAsync(string repositoryRoot, string worktreeRoot)
    {
        var remove = await RunGitAsync(repositoryRoot, CancellationToken.None, "worktree", "remove", "--force", worktreeRoot);
        if (Directory.Exists(worktreeRoot))
        {
            try { Directory.Delete(worktreeRoot, recursive: true); }
            catch { }
        }
        if (remove.ExitCode != 0)
            _ = await RunGitAsync(repositoryRoot, CancellationToken.None, "worktree", "prune");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunGitAsync(
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments)
        => RunGitAsync(workingDirectory, ct, arguments, null);

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunGitAsync(
        string workingDirectory,
        CancellationToken ct,
        string[] arguments,
        string? indexPath)
    {
        var hooksDirectory = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "empty-git-hooks");
        Directory.CreateDirectory(hooksDirectory);
        var argv = new string[arguments.Length + 1];
        argv[0] = "git";
        Array.Copy(arguments, 0, argv, 1, arguments.Length);
        var info = NativeCommandRunner.CreateStartInfo(workingDirectory, "git-internal-worktree", argv);
        info.ArgumentList.Insert(0, "commit.gpgsign=false");
        info.ArgumentList.Insert(0, "-c");
        info.ArgumentList.Insert(0, "core.hooksPath=" + hooksDirectory);
        info.ArgumentList.Insert(0, "-c");
        if (!string.IsNullOrWhiteSpace(indexPath))
            info.Environment["GIT_INDEX_FILE"] = indexPath;
        return ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(60), ct);
    }
}
