namespace ProjectBeacon.Cli.Client;

using System.Text.Json;

/// <summary>Persistent identity and landing evidence stored beside each task worktree.</summary>
internal sealed record TaskWorktreeMetadata(
    int Version,
    Guid ProjectId,
    Guid TaskId,
    string RepositoryRoot,
    string WorktreePath,
    string BranchName,
    string? BaseBranch,
    string BaseSha,
    DateTime CreatedAtUtc,
    DateTime? LandedAtUtc = null,
    string? LandingTargetBranch = null,
    string? LandingSourceSha = null,
    string? LandingCommitSha = null);

internal sealed record TaskWorktreeInventoryEntry(
    Guid ProjectId,
    Guid TaskId,
    string BranchName,
    string WorktreePath,
    DateTime CreatedAtUtc,
    DateTime? LandedAtUtc,
    string? LandingTargetBranch,
    string? LandingCommitSha,
    string Status,
    string? Detail,
    int AgeDays,
    bool CleanupEligible);

internal sealed record TaskWorktreeCleanupResult(
    int Removed,
    int Skipped,
    IReadOnlyList<string> Messages);

/// <summary>
/// Lists and prunes persistent task worktrees. Cleanup requires durable landing evidence,
/// a clean and unchanged task branch, reachability from the recorded target, an expired
/// retention period, and an exclusive task lease.
/// </summary>
internal static class TaskWorktreeLifecycle
{
    private const string MetadataSuffix = ".beacon.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ResolveWorktreeRoot(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (!Path.IsPathRooted(configuredRoot))
                throw new InvalidDataException("WorktreeRoot must be an absolute path.");
            return AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(configuredRoot));
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                throw new InvalidOperationException("Could not resolve a per-user task worktree directory.");
            baseDirectory = Path.Combine(profile, ".local", "share");
        }

        return AgentTaskWorktree.ResolvePhysicalPath(
            Path.GetFullPath(Path.Combine(baseDirectory, "ProjectBeacon", "worktrees")));
    }

    public static async Task<IReadOnlyList<TaskWorktreeInventoryEntry>> ListAsync(
        string? configuredRoot,
        int retentionDays,
        CancellationToken ct)
    {
        var root = ResolveWorktreeRoot(configuredRoot);
        if (!Directory.Exists(root))
            return [];

        if (IsReparsePoint(root))
            throw new InvalidDataException("The task worktree root cannot be a symbolic link or junction.");

        var result = new List<TaskWorktreeInventoryEntry>();
        foreach (var projectDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var projectName = Path.GetFileName(projectDirectory);
            if (!Guid.TryParseExact(projectName, "N", out var projectId)
                || IsReparsePoint(projectDirectory))
                continue;

            var beaconDirectory = Path.Combine(projectDirectory, "beacon");
            var taskDirectory = Path.Combine(beaconDirectory, "task");
            if (!Directory.Exists(taskDirectory)
                || IsReparsePoint(beaconDirectory)
                || IsReparsePoint(taskDirectory))
                continue;

            foreach (var metadataPath in Directory.EnumerateFiles(taskDirectory, "*" + MetadataSuffix, SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (IsReparsePoint(metadataPath))
                {
                    result.Add(InvalidEntry(projectId, metadataPath, "Task metadata is a symbolic link or junction."));
                    continue;
                }

                TaskWorktreeMetadata? metadata;
                try
                {
                    var json = await File.ReadAllTextAsync(metadataPath, ct);
                    metadata = JsonSerializer.Deserialize<TaskWorktreeMetadata>(json, JsonOptions);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    result.Add(InvalidEntry(projectId, metadataPath, "Task metadata could not be read: " + exception.GetType().Name));
                    continue;
                }

                if (metadata is null || !IsValidMetadata(root, projectId, metadataPath, metadata))
                {
                    result.Add(InvalidEntry(projectId, metadataPath, "Task metadata identity or path does not match its directory."));
                    continue;
                }

                result.Add(await InspectAsync(root, metadataPath, metadata, retentionDays, leaseAlreadyHeld: false, ct));
            }
        }

        return result
            .OrderBy(entry => entry.ProjectId)
            .ThenBy(entry => entry.CreatedAtUtc)
            .ThenBy(entry => entry.TaskId)
            .ToArray();
    }

    public static async Task<TaskWorktreeCleanupResult> CleanupExpiredAsync(
        string? configuredRoot,
        int retentionDays,
        CancellationToken ct)
    {
        if (retentionDays <= 0)
            return new TaskWorktreeCleanupResult(0, 0, ["Cleanup is disabled because retention is set to 0 days."]);

        var entries = await ListAsync(configuredRoot, retentionDays, ct);
        var candidates = entries.Where(entry => entry.CleanupEligible).ToArray();
        var removed = 0;
        var skipped = 0;
        var messages = new List<string>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var root = ResolveWorktreeRoot(configuredRoot);
            var metadataPath = candidate.WorktreePath + MetadataSuffix;
            var leasePath = candidate.WorktreePath + ".lock";
            if (IsReparsePoint(metadataPath) || IsReparsePoint(leasePath))
            {
                skipped++;
                messages.Add($"{candidate.TaskId:N}: metadata or lease path became a symbolic link.");
                continue;
            }

            FileStream? lease = null;
            try
            {
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                skipped++;
                messages.Add($"{candidate.TaskId:N}: task worktree is active.");
                continue;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                skipped++;
                messages.Add($"{candidate.TaskId:N}: task lease could not be acquired.");
                continue;
            }

            var deleteLeaseFile = false;
            try
            {
                if (!File.Exists(metadataPath))
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: task metadata disappeared.");
                    continue;
                }

                TaskWorktreeMetadata? metadata;
                try
                {
                    metadata = JsonSerializer.Deserialize<TaskWorktreeMetadata>(
                        await File.ReadAllTextAsync(metadataPath, ct), JsonOptions);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: task metadata could not be re-read.");
                    continue;
                }

                if (metadata is null || !IsValidMetadata(root, candidate.ProjectId, metadataPath, metadata))
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: task metadata identity changed.");
                    continue;
                }

                var fresh = await InspectAsync(root, metadataPath, metadata, retentionDays, leaseAlreadyHeld: true, ct);
                if (!fresh.CleanupEligible)
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: no longer eligible for cleanup ({fresh.Status}).");
                    continue;
                }

                var repository = metadata.RepositoryRoot;
                var filters = await ReadDisabledFiltersAsync(repository, ct);
                if (filters.Error is not null)
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: Git filter configuration could not be verified.");
                    continue;
                }

                if (Directory.Exists(metadata.WorktreePath))
                {
                    var remove = await Git(repository, filters.Filters!, ct, "worktree", "remove", metadata.WorktreePath);
                    if (!Succeeded(remove) || Directory.Exists(metadata.WorktreePath))
                    {
                        skipped++;
                        messages.Add($"{candidate.TaskId:N}: Git refused to remove the task worktree; no force removal was attempted.");
                        continue;
                    }
                }

                // Re-check both refs after removing the clean worktree. The branch delete is
                // a compare-and-swap against the exact reviewed task tip; no force-delete.
                var branchRef = $"refs/heads/{metadata.BranchName}";
                var branchHead = await Git(repository, filters.Filters!, ct, "rev-parse", "--verify", branchRef);
                var targetHead = await Git(repository, filters.Filters!, ct, "rev-parse", "--verify",
                    $"refs/heads/{metadata.LandingTargetBranch}");
                if (!Succeeded(branchHead) || !Succeeded(targetHead)
                    || !string.Equals(branchHead.StdOut.Trim(), metadata.LandingSourceSha, StringComparison.OrdinalIgnoreCase)
                    || await IsAncestorAsync(repository, filters.Filters!, metadata.LandingCommitSha!, targetHead.StdOut.Trim(), ct) != true
                    || await IsAncestorAsync(repository, filters.Filters!, branchHead.StdOut.Trim(), targetHead.StdOut.Trim(), ct) != true)
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: landing reachability or task branch tip changed during cleanup.");
                    continue;
                }

                var deleteBranch = await Git(repository, filters.Filters!, ct,
                    "update-ref", "-d", branchRef, metadata.LandingSourceSha!);
                if (!Succeeded(deleteBranch))
                {
                    skipped++;
                    messages.Add($"{candidate.TaskId:N}: task branch changed before its compare-and-swap deletion.");
                    continue;
                }

                try { File.Delete(metadataPath); } catch { }
                deleteLeaseFile = true;
                removed++;
                messages.Add($"{candidate.TaskId:N}: removed merged task worktree and branch.");
            }
            finally
            {
                await lease.DisposeAsync();
                if (deleteLeaseFile)
                {
                    try { File.Delete(leasePath); } catch { }
                }
            }
        }

        return new TaskWorktreeCleanupResult(removed, skipped, messages);
    }

    private static async Task<TaskWorktreeInventoryEntry> InspectAsync(
        string root,
        string metadataPath,
        TaskWorktreeMetadata metadata,
        int retentionDays,
        bool leaseAlreadyHeld,
        CancellationToken ct)
    {
        var leasePath = metadata.WorktreePath + ".lock";
        FileStream? lease = null;
        if (!leaseAlreadyHeld)
        {
            try
            {
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return Entry(metadata, "active", "A pipeline command currently holds the task worktree lease.", false);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return Entry(metadata, "unavailable", "The task worktree lease could not be inspected.", false);
            }
        }

        try
        {
            var repository = metadata.RepositoryRoot;
            if (!Directory.Exists(repository) || IsReparsePoint(repository))
                return Entry(metadata, "repository-unavailable", "The source repository is missing or is a symbolic link.", false);

            string[] filters;
            var filterResult = await ReadDisabledFiltersAsync(repository, ct);
            if (filterResult.Error is not null)
                return Entry(metadata, "git-unavailable", "Git filter configuration could not be verified.", false);
            filters = filterResult.Filters!;

            var branchRef = $"refs/heads/{metadata.BranchName}";
            var branchHead = await Git(repository, filters, ct, "rev-parse", "--verify", branchRef);
            if (!Succeeded(branchHead) || !AgentTaskWorktree.IsGitSha(branchHead.StdOut.Trim()))
                return Entry(metadata, "branch-missing", "The recorded task branch no longer exists.", false);
            var branchSha = branchHead.StdOut.Trim();

            if (Directory.Exists(metadata.WorktreePath))
            {
                if (HasReparsePointBetween(root, metadata.WorktreePath))
                    return Entry(metadata, "unsafe-path", "The task worktree path contains a symbolic link or junction.", false);

                var actualRoot = await Git(repository, filters, ct, "-C", metadata.WorktreePath, "rev-parse", "--show-toplevel");
                var actualBranch = await Git(repository, filters, ct, "-C", metadata.WorktreePath, "symbolic-ref", "--quiet", "--short", "HEAD");
                var actualHead = await Git(repository, filters, ct, "-C", metadata.WorktreePath, "rev-parse", "--verify", "HEAD");
                if (!Succeeded(actualRoot) || !Succeeded(actualBranch) || !Succeeded(actualHead)
                    || !SamePath(actualRoot.StdOut.Trim(), metadata.WorktreePath)
                    || !string.Equals(actualBranch.StdOut.Trim(), metadata.BranchName, StringComparison.Ordinal)
                    || !string.Equals(actualHead.StdOut.Trim(), branchSha, StringComparison.OrdinalIgnoreCase))
                    return Entry(metadata, "worktree-mismatch", "The worktree no longer matches its recorded task branch.", false);

                var status = await Git(repository, filters, ct, "-C", metadata.WorktreePath, "status", "--porcelain", "--untracked-files=all");
                if (!Succeeded(status))
                    return Entry(metadata, "status-unavailable", "The task worktree status could not be verified.", false);
                if (!string.IsNullOrWhiteSpace(status.StdOut))
                    return Entry(metadata, "dirty", "Uncommitted or untracked task changes are preserved.", false);
            }
            else
            {
                var worktrees = await Git(repository, filters, ct, "worktree", "list", "--porcelain");
                if (!Succeeded(worktrees))
                    return Entry(metadata, "worktree-list-unavailable", "Git worktree registrations could not be inspected.", false);

                var registered = ParseWorktreeList(worktrees.StdOut);
                if (registered.Any(item => string.Equals(item.Branch, branchRef, StringComparison.Ordinal)
                    && !SamePath(item.Path, metadata.WorktreePath)))
                    return Entry(metadata, "checked-out-elsewhere", "The task branch is checked out in another worktree.", false);
                if (registered.Any(item => SamePath(item.Path, metadata.WorktreePath)))
                    return Entry(metadata, "stale-registration", "Git still registers the missing task worktree path.", false);
            }

            if (metadata.LandedAtUtc is null
                || string.IsNullOrWhiteSpace(metadata.LandingTargetBranch)
                || !AgentTaskWorktree.IsGitSha(metadata.LandingSourceSha ?? string.Empty)
                || !AgentTaskWorktree.IsGitSha(metadata.LandingCommitSha ?? string.Empty))
                return Entry(metadata, "not-landed", "No verified successful landing is recorded.", false);

            if (!string.Equals(branchSha, metadata.LandingSourceSha, StringComparison.OrdinalIgnoreCase))
                return Entry(metadata, "changed-after-landing", "The task branch has commits beyond the recorded landing; it is retained.", false);

            var targetHead = await Git(repository, filters, ct, "rev-parse", "--verify",
                $"refs/heads/{metadata.LandingTargetBranch}");
            if (!Succeeded(targetHead) || !AgentTaskWorktree.IsGitSha(targetHead.StdOut.Trim()))
                return Entry(metadata, "target-missing", "The recorded landing target branch is unavailable.", false);

            if (await IsAncestorAsync(repository, filters, metadata.LandingCommitSha!, targetHead.StdOut.Trim(), ct) != true
                || await IsAncestorAsync(repository, filters, branchSha, targetHead.StdOut.Trim(), ct) != true)
                return Entry(metadata, "landing-not-in-target", "The recorded landing or task branch is not reachable from its target.", false);

            var ageDays = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - metadata.LandedAtUtc.Value).TotalDays));
            if (retentionDays <= 0)
                return Entry(metadata, "landed-retained", "Retention is disabled; the merged branch is kept.", false, ageDays);
            if (metadata.LandedAtUtc.Value.AddDays(retentionDays) > DateTime.UtcNow)
            {
                var remaining = (int)Math.Ceiling((metadata.LandedAtUtc.Value.AddDays(retentionDays) - DateTime.UtcNow).TotalDays);
                return Entry(metadata, "landed-retained", $"Eligible for cleanup in approximately {remaining} day(s).", false, ageDays);
            }

            return Entry(metadata, "expired-merged", "The clean task branch is merged and past its retention period.", true, ageDays);
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }

    private static TaskWorktreeInventoryEntry Entry(
        TaskWorktreeMetadata metadata,
        string status,
        string? detail,
        bool cleanupEligible,
        int? ageDays = null) =>
        new(
            metadata.ProjectId,
            metadata.TaskId,
            metadata.BranchName,
            metadata.WorktreePath,
            metadata.CreatedAtUtc,
            metadata.LandedAtUtc,
            metadata.LandingTargetBranch,
            metadata.LandingCommitSha,
            status,
            detail,
            ageDays ?? 0,
            cleanupEligible);

    private static TaskWorktreeInventoryEntry InvalidEntry(Guid projectId, string metadataPath, string detail)
    {
        var name = Path.GetFileName(metadataPath);
        var taskText = name.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^MetadataSuffix.Length]
            : string.Empty;
        _ = Guid.TryParseExact(taskText, "N", out var taskId);
        var worktreePath = metadataPath.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase)
            ? metadataPath[..^MetadataSuffix.Length]
            : metadataPath;
        return new TaskWorktreeInventoryEntry(
            projectId, taskId, taskId == Guid.Empty ? "(unknown)" : $"beacon/task/{taskId:N}",
            worktreePath, DateTime.MinValue, null, null, null,
            "invalid-metadata", detail, 0, false);
    }

    private static bool IsValidMetadata(
        string root,
        Guid projectId,
        string metadataPath,
        TaskWorktreeMetadata metadata)
    {
        if (metadata.Version != 1 || projectId == Guid.Empty || metadata.ProjectId != projectId
            || metadata.TaskId == Guid.Empty
            || !string.Equals(metadata.BranchName, $"beacon/task/{metadata.TaskId:N}", StringComparison.Ordinal)
            || !Path.IsPathRooted(metadata.RepositoryRoot)
            || !Path.IsPathRooted(metadata.WorktreePath)
            || !AgentTaskWorktree.IsGitSha(metadata.BaseSha))
            return false;

        var hasLandingEvidence = metadata.LandedAtUtc is not null
            || metadata.LandingTargetBranch is not null
            || metadata.LandingSourceSha is not null
            || metadata.LandingCommitSha is not null;
        if (hasLandingEvidence
            && (metadata.LandedAtUtc is null
                || !IsSafeBranchName(metadata.LandingTargetBranch)
                || !string.Equals(metadata.LandingTargetBranch, metadata.BaseBranch, StringComparison.Ordinal)
                || !AgentTaskWorktree.IsGitSha(metadata.LandingSourceSha ?? string.Empty)
                || !AgentTaskWorktree.IsGitSha(metadata.LandingCommitSha ?? string.Empty)))
            return false;

        var expectedPath = Path.GetFullPath(Path.Combine(root, projectId.ToString("N"),
            "beacon", "task", metadata.TaskId.ToString("N")));
        return SamePath(expectedPath, metadata.WorktreePath)
            && SamePath(metadataPath, metadata.WorktreePath + MetadataSuffix)
            && !IsWithin(metadata.RepositoryRoot, expectedPath);
    }

    private static async Task<(string[]? Filters, string? Error)> ReadDisabledFiltersAsync(
        string repository,
        CancellationToken ct)
    {
        var result = await Git(repository, [], ct, "config", "--name-only", "--get-regexp", "^filter\\.");
        if (result.TimedOut || result.ExitCode is not (0 or 1))
            return (null, "Git filter configuration could not be inspected.");
        return (AgentSessionWorktree.ParseFilterDriverNames(result.StdOut), null);
    }

    private static async Task<bool?> IsAncestorAsync(
        string repository,
        IReadOnlyCollection<string> filters,
        string ancestor,
        string descendant,
        CancellationToken ct)
    {
        var result = await Git(repository, filters, ct, "merge-base", "--is-ancestor", ancestor, descendant);
        return result.ExitCode == 0 && !result.TimedOut ? true
            : result.ExitCode == 1 && !result.TimedOut ? false
            : null;
    }

    private static IReadOnlyList<(string Path, string? Branch)> ParseWorktreeList(string output)
    {
        var result = new List<(string Path, string? Branch)>();
        foreach (var block in output.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            string? path = null;
            string? branch = null;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.TrimEnd('\r');
                if (value.StartsWith("worktree ", StringComparison.Ordinal))
                    path = value["worktree ".Length..];
                else if (value.StartsWith("branch ", StringComparison.Ordinal))
                    branch = value["branch ".Length..];
            }
            if (!string.IsNullOrWhiteSpace(path))
                result.Add((path, branch));
        }
        return result;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> Git(
        string directory,
        IReadOnlyCollection<string> filters,
        CancellationToken ct,
        params string[] arguments) =>
        AgentSessionWorktree.RunGitAsync(directory, ct, filters, arguments);

    private static bool Succeeded((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
        result.ExitCode == 0 && !result.TimedOut;

    private static bool IsReparsePoint(string path) =>
        (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparsePointBetween(string root, string path)
    {
        if (!IsWithin(root, path))
            return true;

        var current = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(path));
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                return true;
        }
        return false;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsSafeBranchName(string? value) =>
        value is { Length: > 0 and <= 200 }
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && value.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..")
            && !segment.StartsWith(".", StringComparison.Ordinal)
            && !segment.EndsWith(".", StringComparison.Ordinal)
            && segment.All(character => !char.IsControl(character)
                && character is not (' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')));

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
