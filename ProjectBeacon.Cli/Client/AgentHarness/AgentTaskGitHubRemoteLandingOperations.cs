namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using Application.Devices;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Read-only GitHub merge verification for the daemon's AutoSync cycle.
/// This class never pushes, marks PRs ready, merges PRs, or changes target refs.
/// </summary>
internal static class AgentTaskGitHubRemoteLandingOperations
{
    internal sealed record VerificationResult(
        DeviceRemoteLandingReconciliationRequest? Request,
        string? Status);

    private sealed record WorktreeEntry(string Path, string? Branch);

    public static async Task<VerificationResult> VerifyAsync(
        string localRoot,
        Guid projectId,
        DeviceRemoteLandingTargetDto target,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty || target.TaskId == Guid.Empty
            || target.ReviewRunId == Guid.Empty
            || !AgentTaskGitOperations.IsGitSha(target.SourceSha)
            || !string.Equals(target.SourceBranch, $"beacon/task/{target.TaskId:N}", StringComparison.Ordinal))
            return new VerificationResult(null, "Remote landing target did not match the deterministic task branch and Review proof.");

        var rootResult = await AgentTaskGitOperations.Git(
            localRoot, Array.Empty<string>(), ct, "rev-parse", "--show-toplevel");
        if (!AgentTaskGitOperations.Succeeded(rootResult) || string.IsNullOrWhiteSpace(rootResult.StdOut))
            return new VerificationResult(null, "Remote landing skipped: project checkout is not available as a Git repository.");
        var repositoryRoot = Path.GetFullPath(rootResult.StdOut.Trim());

        var branchFormat = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "check-ref-format", "--branch", target.SourceBranch);
        var baseFormat = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "check-ref-format", "--branch", target.TargetBranch);
        if (!AgentTaskGitOperations.Succeeded(branchFormat)
            || !AgentTaskGitOperations.Succeeded(baseFormat))
            return new VerificationResult(null, "Remote landing skipped: task source or review base branch is invalid.");

        var worktrees = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "worktree", "list", "--porcelain");
        if (!AgentTaskGitOperations.Succeeded(worktrees))
            return new VerificationResult(null, "Remote landing skipped: could not inspect registered task worktrees.");
        var matchingWorktrees = ParseWorktrees(worktrees.StdOut)
            .Where(item => string.Equals(item.Branch, "refs/heads/" + target.SourceBranch, StringComparison.Ordinal))
            .ToArray();
        if (matchingWorktrees.Length != 1)
            return new VerificationResult(null, null);
        var taskWorktree = Path.GetFullPath(matchingWorktrees[0].Path);
        if (!Directory.Exists(taskWorktree))
            return new VerificationResult(null, "Remote landing skipped: the registered task worktree is unavailable.");

        var taskBranch = await AgentTaskGitOperations.Git(
            taskWorktree, Array.Empty<string>(), ct, "branch", "--show-current");
        var taskHead = await AgentTaskGitOperations.Git(
            taskWorktree, Array.Empty<string>(), ct, "rev-parse", "--verify", "HEAD");
        var taskStatus = await AgentTaskGitOperations.Git(
            taskWorktree, Array.Empty<string>(), ct, "status", "--porcelain", "--untracked-files=all");
        if (!AgentTaskGitOperations.Succeeded(taskBranch)
            || !AgentTaskGitOperations.Succeeded(taskHead)
            || !AgentTaskGitOperations.Succeeded(taskStatus)
            || !string.Equals(taskBranch.StdOut.Trim(), target.SourceBranch, StringComparison.Ordinal)
            || !string.Equals(taskHead.StdOut.Trim(), target.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(taskStatus.StdOut))
            return new VerificationResult(null, null);

        var origin = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "remote", "get-url", "--all", "origin");
        if (!AgentTaskGitOperations.Succeeded(origin))
            return new VerificationResult(null, null);
        var originUrls = SplitLines(origin.StdOut);
        if (originUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(originUrls[0], out var repository))
            return new VerificationResult(null, null);

        var safePath = NativeCommandRunner.BuildSafePath(
            Environment.GetEnvironmentVariable("PATH"), repositoryRoot);
        var ghPath = WorkstationProbe.Which("gh", safePath);
        if (ghPath is null)
            return new VerificationResult(null, null);

        var auth = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, repositoryRoot, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return new VerificationResult(null, null);

        var listed = await AgentTaskGitOperations.ListPullRequestsAsync(
            ghPath, repositoryRoot, repository, target.SourceBranch, ct);
        if (listed.Error is not null)
            return new VerificationResult(null, "Remote landing could not read the GitHub PR list: " + listed.Error);

        var matchingMerged = listed.PullRequests
            .Where(pull => pull.State == "MERGED"
                && string.Equals(pull.BaseRefName, target.TargetBranch, StringComparison.Ordinal))
            .ToArray();
        if (matchingMerged.Length == 0)
            return new VerificationResult(null, null);
        if (matchingMerged.Length != 1)
            return new VerificationResult(null, "Remote landing skipped: more than one merged PR matches the task branch and review base.");

        var pullRequest = matchingMerged[0];
        var firstSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, repositoryRoot, repository, pullRequest.Number, ct);
        if (firstSnapshot.Error is not null)
            return new VerificationResult(null, "Remote landing could not read the merged PR proof: " + firstSnapshot.Error);

        var view = firstSnapshot.View!;
        if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                view, "MERGED", target.TargetBranch, target.SourceSha)
            || !string.Equals(view.Url, pullRequest.Url, StringComparison.Ordinal)
            || !AgentTaskGitOperations.IsGitSha(view.MergeCommitSha ?? string.Empty)
            || !DateTimeOffset.TryParse(
                view.MergedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var mergedAt))
            return new VerificationResult(null, null);

        var fetch = await AgentTaskGitOperations.RunRemoteGitAsync(
            repositoryRoot, Array.Empty<string>(), ct, originUrls[0],
            "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
            "--", originUrls[0],
            $"+refs/heads/{target.TargetBranch}:refs/remotes/origin/{target.TargetBranch}");
        if (!AgentTaskGitOperations.Succeeded(fetch))
            return new VerificationResult(null, "Remote landing could not fetch the configured GitHub target branch.");

        var remoteBase = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "rev-parse", "--verify",
            $"refs/remotes/origin/{target.TargetBranch}");
        if (!AgentTaskGitOperations.Succeeded(remoteBase)
            || !AgentTaskGitOperations.IsGitSha(remoteBase.StdOut.Trim()))
            return new VerificationResult(null, "Remote landing could not resolve the fetched origin target branch.");
        var remoteBaseSha = remoteBase.StdOut.Trim();

        var mergeAncestor = await AgentTaskGitOperations.Git(
            repositoryRoot, Array.Empty<string>(), ct, "merge-base", "--is-ancestor",
            view.MergeCommitSha!, remoteBaseSha);
        if (!AgentTaskGitOperations.Succeeded(mergeAncestor))
            return new VerificationResult(null, null);

        // Re-read GitHub after fetch; never combine a stale PR snapshot with a newer base ref.
        var finalSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, repositoryRoot, repository, pullRequest.Number, ct);
        if (finalSnapshot.Error is not null)
            return new VerificationResult(null, "Remote landing could not re-confirm the GitHub PR after fetching its base.");
        var finalView = finalSnapshot.View!;
        if (!AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(view, finalView)
            || !string.Equals(view.MergedAt, finalView.MergedAt, StringComparison.Ordinal)
            || !string.Equals(view.MergeCommitSha, finalView.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            return new VerificationResult(null, "Remote landing skipped: the GitHub PR changed while merge ancestry was being verified.");

        // The task worktree must still point at the exact reviewed head and remain clean.
        var finalHead = await AgentTaskGitOperations.Git(
            taskWorktree, Array.Empty<string>(), ct, "rev-parse", "--verify", "HEAD");
        var finalStatus = await AgentTaskGitOperations.Git(
            taskWorktree, Array.Empty<string>(), ct, "status", "--porcelain", "--untracked-files=all");
        if (!AgentTaskGitOperations.Succeeded(finalHead)
            || !AgentTaskGitOperations.Succeeded(finalStatus)
            || !string.Equals(finalHead.StdOut.Trim(), target.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(finalStatus.StdOut))
            return new VerificationResult(null, null);

        var request = new DeviceRemoteLandingReconciliationRequest(
            projectId,
            target.TaskId,
            target.ReviewRunId,
            repository,
            pullRequest.Number,
            finalView.Url,
            target.SourceBranch,
            target.TargetBranch,
            target.SourceSha,
            finalView.MergeCommitSha!,
            remoteBaseSha,
            mergedAt.UtcDateTime,
            DateTime.UtcNow);
        return new VerificationResult(request, $"GitHub PR #{pullRequest.Number} merge verified for task {target.TaskId:D}.");
    }

    internal static IReadOnlyList<WorktreeEntry> ParseWorktrees(string output)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null;
        string? branch = null;
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (path is not null)
                    entries.Add(new WorktreeEntry(path, branch));
                path = line["worktree ".Length..];
                branch = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line["branch ".Length..];
            }
        }
        if (path is not null)
            entries.Add(new WorktreeEntry(path, branch));
        return entries;
    }

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
