namespace ProjectBeacon.Cli.Client;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// A durable worktree and branch shared by every Actor session for one task.
/// It intentionally does not register with GitRepositoryOperationGate: task branches
/// are isolated from the user's checkout and from other tasks' worktrees.
/// </summary>
internal sealed class AgentTaskWorktree : IAgentSessionWorktree
{
    private sealed record Metadata(
        int Version,
        Guid ProjectId,
        Guid TaskId,
        string RepositoryRoot,
        string WorktreePath,
        string BranchName,
        string? BaseBranch,
        string BaseSha,
        DateTime CreatedAtUtc);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _repositoryRoot;
    private readonly string _worktreeRoot;
    private readonly string[] _disabledFilterDrivers;
    private readonly Guid _taskId;
    private readonly FileStream _worktreeLease;
    private bool _disposed;

    private AgentTaskWorktree(
        string repositoryRoot,
        string projectRoot,
        string worktreeRoot,
        string workingDirectory,
        string branchName,
        string? baseBranch,
        string baseSha,
        Guid taskId,
        IReadOnlyCollection<string> disabledFilterDrivers,
        FileStream worktreeLease)
    {
        _repositoryRoot = repositoryRoot;
        ProjectRoot = projectRoot;
        _worktreeRoot = worktreeRoot;
        WorkingDirectory = workingDirectory;
        BranchName = branchName;
        BaseBranch = baseBranch;
        BaseSha = baseSha;
        _taskId = taskId;
        _disabledFilterDrivers = disabledFilterDrivers.ToArray();
        _worktreeLease = worktreeLease;
    }

    public string WorkingDirectory { get; }
    public string ProjectRoot { get; }
    public string RecoveryPath => _worktreeRoot;
    public string BranchName { get; }
    public string? BaseBranch { get; }
    public string BaseSha { get; }

    public string BuildEnvironmentContract()
    {
        var relativeProjectRoot = Path.GetRelativePath(_repositoryRoot, ProjectRoot)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var builder = new StringBuilder();
        builder.AppendLine("## Persistent task branch workspace");
        builder.AppendLine("This session shares a persistent Git worktree with every later Actor session for this task.");
        builder.AppendLine("Task branch: " + BranchName);
        builder.AppendLine("Base branch: " + (BaseBranch ?? "(detached HEAD)"));
        builder.AppendLine("Base commit: " + BaseSha);
        builder.AppendLine("Repository-relative project root: " + (relativeProjectRoot == "." ? "." : relativeProjectRoot));
        builder.AppendLine("The worktree starts from a committed base. Uncommitted changes in the user's original checkout are not included.");
        builder.AppendLine("The daemon commits the task worktree after each completed Actor session. Do not use raw git commands or change branches.");
        builder.AppendLine("The task branch and worktree are persistent. Finishing or cancelling this session does not delete them.");
        return builder.ToString().TrimEnd();
    }

    public void Preserve()
    {
        // Branch worktrees are durable by design. DisposeAsync still commits outstanding
        // changes as WIP so cancellation and agent failures do not discard work.
    }

    public Task<string> BuildDiffArtifactAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // In Branch mode the commit SHA is the durable diff reference. A duplicate patch
        // artifact would be redundant and potentially truncated.
        return Task.FromResult(string.Empty);
    }

    public Task<(bool Success, string? Error, string? CommitSha)> CompleteChangesAsync(
        string commitMessage,
        CancellationToken ct) =>
        CommitChangesAsync(commitMessage, ct);

    public static async Task<(AgentTaskWorktree? Worktree, string? Error, string? Warning)> CreateAsync(
        string projectDirectory,
        Guid projectId,
        Guid taskId,
        string? configuredWorktreeRoot,
        CancellationToken ct)
    {
        string? warning = null;
        FileStream? lease = null;

        (AgentTaskWorktree? Worktree, string? Error, string? Warning) Fail(string error)
        {
            lease?.Dispose();
            lease = null;
            return (null, error, warning);
        }

        if (projectId == Guid.Empty || taskId == Guid.Empty)
            return Fail("Project and task identifiers are required.");
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            return Fail("The project directory does not exist.");

        try
        {
            var projectRoot = Path.GetFullPath(projectDirectory);
            var rootResult = await AgentSessionWorktree.RunGitAsync(
                projectRoot, ct, [], "rev-parse", "--show-toplevel");
            if (rootResult.ExitCode != 0 || rootResult.TimedOut || string.IsNullOrWhiteSpace(rootResult.StdOut))
                return Fail("Task branch workspaces require a Git repository.");

            var repositoryRoot = Path.GetFullPath(rootResult.StdOut.Trim());
            var prefixResult = await AgentSessionWorktree.RunGitAsync(
                projectRoot, ct, [], "rev-parse", "--show-prefix");
            if (prefixResult.ExitCode != 0 || prefixResult.TimedOut)
                return Fail("Could not resolve the project directory inside its Git repository.");

            var versionResult = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, [], "--version");
            if (versionResult.ExitCode != 0 || versionResult.TimedOut
                || !SupportsBranchWorkflow(versionResult.StdOut))
                return Fail("Task branch workspaces require Git 2.38 or newer.");

            var filterConfig = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, [], "config", "--name-only", "--get-regexp", "^filter\\.");
            if (filterConfig.TimedOut || filterConfig.ExitCode is not (0 or 1))
                return Fail("Could not inspect Git filter configuration safely.");
            var disabledFilters = AgentSessionWorktree.ParseFilterDriverNames(filterConfig.StdOut);

            var headResult = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, disabledFilters, "rev-parse", "--verify", "HEAD");
            if (headResult.ExitCode != 0 || headResult.TimedOut || !IsGitSha(headResult.StdOut.Trim()))
                return Fail("The Git repository needs an initial commit before a task worktree can be created.");
            var baseSha = headResult.StdOut.Trim();

            var branchResult = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, disabledFilters, "symbolic-ref", "--quiet", "--short", "HEAD");
            string? baseBranch = branchResult.ExitCode == 0
                ? branchResult.StdOut.Trim()
                : null;
            if (branchResult.ExitCode is not (0 or 1) || branchResult.TimedOut)
                return Fail("Could not identify the current base branch safely.");

            if (!string.IsNullOrWhiteSpace(baseBranch))
            {
                var remote = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, ct, disabledFilters, "remote", "get-url", "origin");
                if (remote.ExitCode == 0 && !string.IsNullOrWhiteSpace(remote.StdOut))
                {
                    var fetch = await AgentSessionWorktree.RunGitAsync(
                        repositoryRoot, ct, disabledFilters,
                        "fetch", "--no-tags", "origin",
                        $"refs/heads/{baseBranch}:refs/remotes/origin/{baseBranch}");
                    if (fetch.ExitCode == 0 && !fetch.TimedOut)
                    {
                        var remoteHead = await AgentSessionWorktree.RunGitAsync(
                            repositoryRoot, ct, disabledFilters,
                            "rev-parse", "--verify", $"refs/remotes/origin/{baseBranch}");
                        if (remoteHead.ExitCode == 0 && IsGitSha(remoteHead.StdOut.Trim()))
                            baseSha = remoteHead.StdOut.Trim();
                        else
                            warning = "The origin branch was fetched, but its commit could not be resolved; using the local HEAD.";
                    }
                    else
                    {
                        warning = "Could not fetch origin; using the local committed HEAD. " + Clip(fetch.StdErr);
                    }
                }
                else
                {
                    warning = "No origin remote is configured; using the local committed HEAD.";
                }
            }
            else
            {
                warning = "The original checkout is detached; using its current committed HEAD as the task base.";
            }

            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                    return Fail("Could not resolve a persistent per-user worktree directory.");
                localApplicationData = Path.Combine(userProfile, ".local", "share");
            }

            var worktreeRootSetting = string.IsNullOrWhiteSpace(configuredWorktreeRoot)
                ? Path.Combine(localApplicationData, "ProjectBeacon", "worktrees")
                : configuredWorktreeRoot.Trim();
            if (!Path.IsPathRooted(worktreeRootSetting))
                return Fail("WorktreeRoot must be an absolute path.");
            var worktreeRoot = Path.GetFullPath(worktreeRootSetting);
            if (IsWithin(repositoryRoot, worktreeRoot))
                return Fail("WorktreeRoot must be outside the source repository.");

            var branchName = $"beacon/task/{taskId:N}";
            var worktreePath = Path.GetFullPath(Path.Combine(
                worktreeRoot,
                projectId.ToString("N"),
                "beacon",
                "task",
                taskId.ToString("N")));
            var relativeWorktree = Path.GetRelativePath(worktreeRoot, worktreePath);
            if (Path.IsPathRooted(relativeWorktree) || relativeWorktree == ".."
                || relativeWorktree.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relativeWorktree.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                return Fail("The task worktree path escapes WorktreeRoot.");

            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            if ((Directory.Exists(worktreeRoot)
                    && (File.GetAttributes(worktreeRoot) & FileAttributes.ReparsePoint) != 0)
                || HasReparsePointBetween(worktreeRoot, Path.GetDirectoryName(worktreePath)!))
                return Fail("The configured task worktree path contains a symbolic link or junction.");

            var metadataPath = worktreePath + ".beacon.json";
            var leasePath = worktreePath + ".lock";
            try
            {
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return Fail("This task's branch worktree is already in use by another pipeline command.");
            }

            Metadata metadata;
            if (File.Exists(metadataPath))
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                metadata = JsonSerializer.Deserialize<Metadata>(json, JsonOptions)
                    ?? throw new InvalidDataException("Task worktree metadata is empty.");
                if (metadata.Version != 1
                    || metadata.ProjectId != projectId
                    || metadata.TaskId != taskId
                    || !string.Equals(metadata.RepositoryRoot, repositoryRoot, PathComparison)
                    || !string.Equals(metadata.WorktreePath, worktreePath, PathComparison)
                    || !string.Equals(metadata.BranchName, branchName, StringComparison.Ordinal)
                    || !IsGitSha(metadata.BaseSha))
                    return Fail("Existing task worktree metadata does not match this project and repository.");
                baseBranch = metadata.BaseBranch;
                baseSha = metadata.BaseSha;
            }
            else
            {
                if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
                    return Fail("A task worktree directory exists without ProjectBeacon metadata; it will not be reused.");

                var branchExists = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, ct, disabledFilters,
                    "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}");
                if (branchExists.ExitCode == 0)
                    return Fail("The task branch already exists without ProjectBeacon metadata; refusing to attach it implicitly.");
                if (branchExists.ExitCode != 1 || branchExists.TimedOut)
                    return Fail("Could not verify whether the task branch already exists.");

                metadata = new Metadata(
                    1, projectId, taskId, repositoryRoot, worktreePath,
                    branchName, baseBranch, baseSha, DateTime.UtcNow);
                var tempMetadata = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(
                    tempMetadata, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false), ct);
                File.Move(tempMetadata, metadataPath);
            }

            if (Directory.Exists(worktreePath))
            {
                if (HasReparsePointBetween(worktreeRoot, worktreePath))
                    return Fail("The existing task worktree contains a symbolic link or junction in its path.");
                var actualRoot = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, ct, disabledFilters, "-C", worktreePath, "rev-parse", "--show-toplevel");
                var actualBranch = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, ct, disabledFilters, "-C", worktreePath, "symbolic-ref", "--quiet", "--short", "HEAD");
                if (actualRoot.ExitCode != 0 || actualBranch.ExitCode != 0
                    || !string.Equals(Path.GetFullPath(actualRoot.StdOut.Trim()), worktreePath, PathComparison)
                    || !string.Equals(actualBranch.StdOut.Trim(), branchName, StringComparison.Ordinal))
                    return Fail("The existing task worktree is not checked out on its expected ProjectBeacon branch.");
            }
            else
            {
                var branchExists = await AgentSessionWorktree.RunGitAsync(
                    repositoryRoot, ct, disabledFilters,
                    "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}");
                (int ExitCode, string StdOut, string StdErr, bool TimedOut) add;
                if (branchExists.ExitCode == 0)
                {
                    add = await AgentSessionWorktree.RunGitAsync(
                        repositoryRoot, ct, disabledFilters, "worktree", "add", worktreePath, branchName);
                }
                else if (branchExists.ExitCode == 1)
                {
                    add = await AgentSessionWorktree.RunGitAsync(
                        repositoryRoot, ct, disabledFilters, "worktree", "add", "-b", branchName, worktreePath, baseSha);
                }
                else
                {
                    return Fail("Could not inspect the task branch before creating its worktree.");
                }

                if (add.ExitCode != 0 || add.TimedOut)
                {
                    // Recover a branch created just before an interrupted/racing worktree add.
                    var nowExists = await AgentSessionWorktree.RunGitAsync(
                        repositoryRoot, ct, disabledFilters,
                        "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}");
                    if (nowExists.ExitCode == 0 && !Directory.Exists(worktreePath))
                    {
                        add = await AgentSessionWorktree.RunGitAsync(
                            repositoryRoot, ct, disabledFilters, "worktree", "add", worktreePath, branchName);
                    }
                }
                if (add.ExitCode != 0 || add.TimedOut)
                    return Fail("Could not create the persistent task worktree. " + Clip(add.StdErr));
            }

            var verifiedHead = await AgentSessionWorktree.RunGitAsync(
                repositoryRoot, ct, disabledFilters, "-C", worktreePath, "rev-parse", "--verify", "HEAD");
            if (verifiedHead.ExitCode != 0 || verifiedHead.TimedOut || !IsGitSha(verifiedHead.StdOut.Trim()))
                return Fail("Could not verify the task worktree's starting commit.");

            var relativeProject = prefixResult.StdOut.Trim()
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var workingDirectory = string.IsNullOrWhiteSpace(relativeProject)
                ? worktreePath
                : Path.GetFullPath(Path.Combine(worktreePath, relativeProject));
            if (!IsWithin(worktreePath, workingDirectory) || !Directory.Exists(workingDirectory))
                return Fail("The project directory inside the task worktree is missing or escapes the worktree.");

            var created = new AgentTaskWorktree(
                repositoryRoot, projectRoot, worktreePath, workingDirectory,
                branchName, baseBranch, baseSha, taskId, disabledFilters, lease);
            lease = null;
            return (created, null, warning);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lease?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        {
            lease?.Dispose();
            return (null, "Could not open the persistent task worktree (" + ex.GetType().Name + "): " + Clip(ex.Message), warning);
        }
    }

    public async Task<(bool Success, string? Error, string? CommitSha)> CommitChangesAsync(
        string commitMessage,
        CancellationToken ct)
    {
        var status = await AgentSessionWorktree.RunGitAsync(
            _worktreeRoot, ct, _disabledFilterDrivers, "status", "--porcelain", "--untracked-files=all");
        if (status.ExitCode != 0 || status.TimedOut)
            return (false, "Could not inspect the persistent task worktree.", null);

        if (string.IsNullOrWhiteSpace(status.StdOut))
            return await ReadHeadAsync(ct);

        var stage = await AgentSessionWorktree.RunGitAsync(
            _worktreeRoot, ct, _disabledFilterDrivers, "add", "-A", "--", ".");
        if (stage.ExitCode != 0 || stage.TimedOut)
            return (false, "Could not stage task worktree changes.", null);

        var summary = await AgentSessionWorktree.RunGitAsync(
            _worktreeRoot, ct, _disabledFilterDrivers, "diff", "--cached", "--summary", "HEAD");
        if (summary.ExitCode != 0 || summary.TimedOut)
            return (false, "Could not inspect staged task worktree changes.", null);
        if (summary.StdOut.Contains("120000", StringComparison.Ordinal))
            return (false, "Task worktree contains a symbolic link; changes remain preserved but were not committed.", null);

        var message = string.IsNullOrWhiteSpace(commitMessage)
            ? $"ProjectBeacon: task {_taskId:N}"
            : commitMessage.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 180)
            message = message[..180];

        var commit = await AgentSessionWorktree.RunGitAsync(
            _worktreeRoot, ct, _disabledFilterDrivers,
            "-c", "user.name=ProjectBeacon",
            "-c", "user.email=projectbeacon@localhost",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", message);
        if (commit.ExitCode != 0 || commit.TimedOut)
            return (false, "Could not commit task worktree changes. " + Clip(commit.StdErr), null);

        return await ReadHeadAsync(ct);
    }

    private async Task<(bool Success, string? Error, string? CommitSha)> ReadHeadAsync(CancellationToken ct)
    {
        var head = await AgentSessionWorktree.RunGitAsync(
            _worktreeRoot, ct, _disabledFilterDrivers, "rev-parse", "--verify", "HEAD");
        return head.ExitCode == 0 && !head.TimedOut && IsGitSha(head.StdOut.Trim())
            ? (true, null, head.StdOut.Trim())
            : (false, "Could not read the task worktree commit after completion.", null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            var recovery = await CommitChangesAsync(
                $"ProjectBeacon: WIP task {_taskId:N}", CancellationToken.None);
            if (!recovery.Success)
                throw new InvalidOperationException(recovery.Error ?? "Could not preserve task worktree changes.");
        }
        finally
        {
            await _worktreeLease.DisposeAsync();
        }
    }

    internal static bool SupportsBranchWorkflow(string versionOutput)
    {
        var match = Regex.Match(versionOutput ?? string.Empty, @"(?<major>\d+)\.(?<minor>\d+)");
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor))
            return false;
        return major > 2 || major == 2 && minor >= 38;
    }

    private static bool IsGitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!IsWithin(fullRoot, fullPath))
            return true;
        var current = fullRoot;
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static string Clip(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= 400 ? normalized : normalized[..400] + "…";
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
