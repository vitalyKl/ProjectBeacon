namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    internal sealed record WorktreeEntry(string Path, string? Branch);

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
        using var repositoryGate = await GitRepositoryOperationGate.AcquireAsync(repositoryRoot, ct);
        if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
            return new VerificationResult(null, null);

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

        var matching = listed.PullRequests
            .Where(pull => string.Equals(pull.BaseRefName, target.TargetBranch, StringComparison.Ordinal))
            .ToArray();
        var matchingMerged = matching.Where(pull => pull.State == "MERGED").ToArray();
        if (matchingMerged.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(target.MergeRequestId))
            {
                var open = matching.Where(pull => pull.State == "OPEN").ToArray();
                if (open.Length > 1)
                    return new VerificationResult(null, "Remote landing skipped: more than one open PR matches the queued merge request.");
                if (open.Length == 1)
                {
                    var asyncState = await ReadAsyncMergeRequestStateAsync(
                        ghPath, repositoryRoot, repository, open[0].Number, target.MergeRequestId, ct);
                    return new VerificationResult(null, asyncState.StatusMessage);
                }
            }
            return new VerificationResult(null, null);
        }
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

        if (!string.IsNullOrWhiteSpace(target.MergeRequestId))
        {
            var asyncState = await ReadAsyncMergeRequestStateAsync(
                ghPath, repositoryRoot, repository, pullRequest.Number, target.MergeRequestId, ct);
            if (asyncState.MergeCommitSha is not null
                && !string.Equals(asyncState.MergeCommitSha, view.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
                return new VerificationResult(null, "Remote landing skipped: async merge result SHA does not match the merged PR.");
            // A missing/expired async request record does not override the authoritative
            // merged PR state; GitHub retains these records only for a limited period.
        }

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

        int? postMergeCheckExitCode = null;
        string? postMergeCheckOutputHash = null;
        if (target.MergeRequestId is not null)
        {
            if (target.LandingAttemptId is null
                || !AgentTaskGitOperations.IsGitSha(target.CandidateTreeSha ?? string.Empty)
                || string.IsNullOrWhiteSpace(target.PostMergeCheckCommand))
                return new VerificationResult(null, "Remote landing skipped: queued Beacon merge is missing its candidate tree or pinned post-merge check command.");

            var mergeTree = await AgentTaskGitOperations.Git(
                repositoryRoot, Array.Empty<string>(), ct,
                "rev-parse", "--verify", finalView.MergeCommitSha + "^{tree}");
            if (!AgentTaskGitOperations.Succeeded(mergeTree)
                || !string.Equals(mergeTree.StdOut.Trim(), target.CandidateTreeSha, StringComparison.OrdinalIgnoreCase))
                return new VerificationResult(null, "Remote landing skipped: queued GitHub merge tree differs from the reviewed candidate tree.");

            var postMergeCheck = await RunPostMergeCheckAsync(
                repositoryRoot, localRoot, finalView.MergeCommitSha!, target.CandidateTreeSha!,
                target.PostMergeCheckCommand, ct);
            if (postMergeCheck.Error is not null || postMergeCheck.ExitCode != 0 || postMergeCheck.OutputHash is null)
                return new VerificationResult(null, "Remote landing skipped: post-merge check failed (" + (postMergeCheck.Error ?? "PostMergeCheckFailed") + ").");
            postMergeCheckExitCode = postMergeCheck.ExitCode;
            postMergeCheckOutputHash = postMergeCheck.OutputHash;
        }

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
            DateTime.UtcNow,
            target.LandingAttemptId,
            target.MergeRequestId,
            target.CandidateTreeSha,
            postMergeCheckExitCode,
            postMergeCheckOutputHash);
        return new VerificationResult(request, $"GitHub PR #{pullRequest.Number} merge verified for task {target.TaskId:D}.");
    }

    private sealed record AsyncMergeState(string? Status, string? MergeCommitSha, string? StatusMessage);

    private static async Task<AsyncMergeState> ReadAsyncMergeRequestStateAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        int pullRequestNumber,
        string mergeRequestId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(mergeRequestId, out var parsed) || parsed == Guid.Empty)
            return new AsyncMergeState(null, null, "Remote landing skipped: GitHub async merge request ID is invalid.");

        var result = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "api", $"repos/{repository}/pulls/{pullRequestNumber}/merge-async/{parsed:D}");
        if (result.ExitCode != 0 || result.TimedOut)
            return new AsyncMergeState(null, null,
                "GitHub async merge request is not currently readable; waiting for the PR merge state. " +
                AgentTaskGitOperations.Clip(result.StdErr.Trim()));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var state)
                && state.ValueKind == System.Text.Json.JsonValueKind.String
                    ? state.GetString()?.ToLowerInvariant()
                    : null;
            var details = root.TryGetProperty("details", out var detailsValue)
                && detailsValue.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? detailsValue
                    : default;
            var mergeSha = details.ValueKind == System.Text.Json.JsonValueKind.Object
                && details.TryGetProperty("sha", out var detailsSha)
                && detailsSha.ValueKind == System.Text.Json.JsonValueKind.String
                    ? detailsSha.GetString()
                    : null;
            var message = details.ValueKind == System.Text.Json.JsonValueKind.Object
                && details.TryGetProperty("message", out var detailsMessage)
                && detailsMessage.ValueKind == System.Text.Json.JsonValueKind.String
                    ? detailsMessage.GetString()
                    : null;
            return status switch
            {
                "pending" => new AsyncMergeState(status, mergeSha, "GitHub async merge is pending."),
                "enqueued" => new AsyncMergeState(status, mergeSha, "GitHub PR is in the merge queue; waiting for the actual merge."),
                "failed" => new AsyncMergeState(status, mergeSha, "GitHub async merge request failed: " + (message ?? "no failure details")),
                "merged" => new AsyncMergeState(status, mergeSha, null),
                _ => new AsyncMergeState(status, mergeSha, "GitHub returned an unsupported async merge request state.")
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new AsyncMergeState(null, null, "GitHub returned malformed async merge request JSON.");
        }
    }

    private sealed record PostMergeCheckResult(int? ExitCode, string? OutputHash, string? Error);

    private static async Task<PostMergeCheckResult> RunPostMergeCheckAsync(
        string repositoryRoot,
        string projectRoot,
        string mergeCommitSha,
        string expectedTreeSha,
        string checkCommand,
        CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-remote-landing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var add = await AgentTaskGitOperations.Git(
                repositoryRoot, Array.Empty<string>(), ct,
                "worktree", "add", "--detach", temp, mergeCommitSha);
            if (!AgentTaskGitOperations.Succeeded(add))
                return new PostMergeCheckResult(null, null, "PostMergeCheckWorktreeFailed");

            var relativeProject = Path.GetRelativePath(repositoryRoot, projectRoot);
            var checkDirectory = Path.GetFullPath(Path.Combine(temp, relativeProject));
            var relativeCheck = Path.GetRelativePath(temp, checkDirectory);
            if (Path.IsPathRooted(relativeCheck)
                || relativeCheck == ".."
                || relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !Directory.Exists(checkDirectory))
                return new PostMergeCheckResult(null, null, "PostMergeCheckProjectMissing");

            var check = EvalCheck.Execute(checkDirectory, checkCommand);
            var output = check.Output ?? string.Empty;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
            var head = await AgentTaskGitOperations.Git(temp, Array.Empty<string>(), ct, "rev-parse", "--verify", "HEAD");
            var tree = await AgentTaskGitOperations.Git(temp, Array.Empty<string>(), ct, "rev-parse", "--verify", "HEAD^{tree}");
            var status = await AgentTaskGitOperations.Git(temp, Array.Empty<string>(), ct, "status", "--porcelain", "--untracked-files=all");
            if (!AgentTaskGitOperations.Succeeded(head)
                || !string.Equals(head.StdOut.Trim(), mergeCommitSha, StringComparison.OrdinalIgnoreCase)
                || !AgentTaskGitOperations.Succeeded(tree)
                || !string.Equals(tree.StdOut.Trim(), expectedTreeSha, StringComparison.OrdinalIgnoreCase)
                || !AgentTaskGitOperations.Succeeded(status)
                || !string.IsNullOrWhiteSpace(status.StdOut))
                return new PostMergeCheckResult(-2, hash, "PostMergeCheckModifiedFiles");
            if (check.ExitCode != 0)
                return new PostMergeCheckResult(check.ExitCode, hash, "PostMergeCheckFailed");
            return new PostMergeCheckResult(0, hash, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PostMergeCheckResult(null, null, "PostMergeCheckError:" + exception.GetType().Name);
        }
        finally
        {
            await AgentTaskGitOperations.Git(
                repositoryRoot, Array.Empty<string>(), CancellationToken.None,
                "worktree", "remove", "--force", temp);
            await AgentTaskGitOperations.Git(
                repositoryRoot, Array.Empty<string>(), CancellationToken.None,
                "worktree", "prune");
        }
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
