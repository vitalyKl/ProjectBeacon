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
        DeviceRemoteLandingFailureRequest? FailureRequest,
        string? Status)
    {
        public VerificationResult(DeviceRemoteLandingReconciliationRequest? request, string? status)
            : this(request, null, status)
        {
        }
    }

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
            var open = matching.Where(pull => pull.State == "OPEN").ToArray();
            if (open.Length > 1)
                return new VerificationResult(null, "Remote landing skipped: more than one open PR matches the queued merge request.");
            if (open.Length == 1 && target.LandingAttemptId is { } landingAttemptId)
            {
                AsyncMergeState? asyncState = null;
                if (!string.IsNullOrWhiteSpace(target.MergeRequestId))
                {
                    asyncState = await ReadAsyncMergeRequestStateAsync(
                        ghPath, repositoryRoot, repository, open[0].Number, target.MergeRequestId, ct);
                    if (string.Equals(asyncState.Status, "failed", StringComparison.Ordinal))
                        return await BuildQueueFailureAsync(
                            ghPath, repositoryRoot, projectId, repository, open[0], target, landingAttemptId,
                            asyncState.StatusMessage ?? "GitHub async merge request failed.", ct);
                    if (string.Equals(asyncState.Status, "pending", StringComparison.Ordinal))
                        return new VerificationResult(null, asyncState.StatusMessage);
                }

                var queueAcknowledged = target.GitHubMergeQueued
                    || string.Equals(asyncState?.Status, "enqueued", StringComparison.Ordinal);
                if (queueAcknowledged)
                {
                    var queue = await ReadMergeQueueEntryAsync(
                        ghPath, repositoryRoot, repository, open[0].Number, ct);
                    if (queue.Error is not null)
                        return new VerificationResult(null,
                            "GitHub confirmed this PR was enqueued, but its current merge-queue state could not be read. " + queue.Error);
                    if (!string.Equals(queue.PullRequestState, "OPEN", StringComparison.Ordinal))
                        return new VerificationResult(null,
                            "GitHub merge-queue status returned a PR state that changed during polling; retrying on the next AutoSync cycle.");
                    if (queue.EntryState is null
                        || string.Equals(queue.EntryState, "UNMERGEABLE", StringComparison.Ordinal))
                        return await BuildQueueFailureAsync(
                            ghPath, repositoryRoot, projectId, repository, open[0], target, landingAttemptId,
                            queue.EntryState is null
                                ? "GitHub removed the PR from its merge queue before it merged."
                                : "GitHub marked the merge-queue entry UNMERGEABLE.",
                            ct);

                    return new VerificationResult(null,
                        $"GitHub PR #{open[0].Number} remains in the merge queue ({queue.EntryState}, position {queue.Position?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}); waiting for MERGED.");
                }

                if (asyncState is not null)
                    return new VerificationResult(null, asyncState.StatusMessage);

                // A generic CommandCompletionUnknown can also come from local GitLand.
                // Do not infer queue removal without an explicit GitHub queue acknowledgement.
                var queuedSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                    ghPath, repositoryRoot, repository, open[0].Number, ct);
                if (queuedSnapshot.Error is not null)
                    return new VerificationResult(null, "Remote landing could not inspect the open PR state.");
                return new VerificationResult(null,
                    $"GitHub PR #{open[0].Number} is still {queuedSnapshot.View!.MergeStateStatus}; waiting for MERGED.");
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
        if (target.LandingAttemptId is not null)
        {
            if (!AgentTaskGitOperations.IsGitSha(target.CandidateTreeSha ?? string.Empty))
                return new VerificationResult(null, "Remote landing skipped: Beacon landing is missing its reviewed candidate tree.");

            var hasPersistedPostMergeProof = target.PersistedPostMergeCheckExitCode is not null
                || !string.IsNullOrWhiteSpace(target.PersistedPostMergeCheckOutputHash);
            var expectedTreeSha = hasPersistedPostMergeProof ? target.CandidateTreeSha : null;
            var mergeTree = await AgentTaskGitOperations.Git(
                repositoryRoot, Array.Empty<string>(), ct,
                "rev-parse", "--verify", finalView.MergeCommitSha + "^{tree}");
            if (!AgentTaskGitOperations.Succeeded(mergeTree)
                || !GitHubLandingMergeExecutor.IsPostMergeTreeAcceptable(expectedTreeSha, mergeTree.StdOut.Trim()))
                return new VerificationResult(null, hasPersistedPostMergeProof
                    ? "Remote landing skipped: direct merge tree differs from the immutable Preview candidate."
                    : "Remote landing skipped: GitHub merge-group tree is unavailable or invalid.");

            if (hasPersistedPostMergeProof)
            {
                // GitLand already ran the pinned check before recording Landed. Reuse
                // that immutable server-persisted evidence; the project setting may have
                // changed since then and must not retroactively invalidate a completed land.
                if (target.PersistedPostMergeCheckExitCode != 0
                    || !IsSha256(target.PersistedPostMergeCheckOutputHash)
                    || !string.IsNullOrWhiteSpace(target.PostMergeCheckCommand))
                    return new VerificationResult(null, "Remote landing skipped: persisted post-merge evidence is incomplete or mixed with a queued check command.");

                postMergeCheckExitCode = target.PersistedPostMergeCheckExitCode;
                postMergeCheckOutputHash = target.PersistedPostMergeCheckOutputHash;
            }
            else
            {
                // A queued async merge has no post-merge evidence yet. Run only the
                // command whose hash still matches the immutable Review proof.
                if (string.IsNullOrWhiteSpace(target.PostMergeCheckCommand))
                    return new VerificationResult(null, "Remote landing skipped: queued Beacon merge is missing its pinned post-merge check command.");

                var postMergeCheck = await RunPostMergeCheckAsync(
                    repositoryRoot, localRoot, finalView.MergeCommitSha!, null,
                    target.PostMergeCheckCommand, ct);
                if (postMergeCheck.Error is not null || postMergeCheck.ExitCode != 0 || postMergeCheck.OutputHash is null)
                    return new VerificationResult(null, "Remote landing skipped: post-merge check failed (" + (postMergeCheck.Error ?? "PostMergeCheckFailed") + ").");
                postMergeCheckExitCode = postMergeCheck.ExitCode;
                postMergeCheckOutputHash = postMergeCheck.OutputHash;
            }
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

    internal sealed record MergeQueueEntryState(string? PullRequestState, string? EntryState, int? Position, string? Error);

    private static async Task<VerificationResult> BuildQueueFailureAsync(
        string ghPath,
        string workingDirectory,
        Guid projectId,
        string repository,
        AgentTaskGitOperations.PullRequestInfo pullRequest,
        DeviceRemoteLandingTargetDto target,
        Guid landingAttemptId,
        string reason,
        CancellationToken ct)
    {
        var snapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, workingDirectory, repository, pullRequest.Number, ct);
        if (snapshot.Error is not null)
            return new VerificationResult(null, "GitHub reported a queue failure, but the exact PR could not be re-confirmed. " + snapshot.Error);
        var view = snapshot.View!;
        if (view.State != "OPEN"
            || !string.Equals(view.BaseBranch, target.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(view.HeadSha, target.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(view.Url, pullRequest.Url, StringComparison.Ordinal))
            return new VerificationResult(null, "GitHub queue failure could not be bound to the exact open PR/source/base identity.");

        var request = new DeviceRemoteLandingFailureRequest(
            projectId,
            target.TaskId,
            landingAttemptId,
            target.ReviewRunId,
            repository,
            pullRequest.Number,
            view.Url,
            target.SourceBranch,
            target.TargetBranch,
            target.SourceSha,
            target.MergeRequestId,
            DateTime.UtcNow);
        return new VerificationResult(null, request, reason);
    }

    internal static (MergeQueueEntryState? State, string? Error) ParseMergeQueueEntryResponse(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == System.Text.Json.JsonValueKind.Array
                && errors.GetArrayLength() > 0)
                return (null, "GitHub GraphQL returned an error while reading the merge queue.");
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != System.Text.Json.JsonValueKind.Object
                || !data.TryGetProperty("repository", out var repository)
                || repository.ValueKind != System.Text.Json.JsonValueKind.Object
                || !repository.TryGetProperty("pullRequest", out var pullRequest)
                || pullRequest.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (null, "GitHub GraphQL returned incomplete repository/PR data for the merge queue.");
            var prState = pullRequest.TryGetProperty("state", out var state)
                && state.ValueKind == System.Text.Json.JsonValueKind.String ? state.GetString() : null;
            if (string.IsNullOrWhiteSpace(prState))
                return (null, "GitHub GraphQL returned no pull request state for the merge queue.");
            if (!pullRequest.TryGetProperty("mergeQueueEntry", out var entry)
                || entry.ValueKind == System.Text.Json.JsonValueKind.Null)
                return (new MergeQueueEntryState(prState, null, null, null), null);
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object
                || !entry.TryGetProperty("state", out var entryState)
                || entryState.ValueKind != System.Text.Json.JsonValueKind.String)
                return (null, "GitHub GraphQL returned an invalid merge queue entry.");
            int? position = entry.TryGetProperty("position", out var positionValue)
                && positionValue.ValueKind == System.Text.Json.JsonValueKind.Number
                && positionValue.TryGetInt32(out var parsedPosition)
                    ? parsedPosition : null;
            return (new MergeQueueEntryState(prState, entryState.GetString(), position, null), null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "GitHub GraphQL returned malformed merge queue JSON.");
        }
    }

    private static async Task<MergeQueueEntryState> ReadMergeQueueEntryAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        int pullRequestNumber,
        CancellationToken ct)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || pullRequestNumber < 1)
            return new MergeQueueEntryState(null, null, null, "The GitHub repository or PR identity is invalid.");
        const string query = "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { state mergeQueueEntry { state position } } } }";
        var result = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "api", "graphql",
            "-f", "query=" + query,
            "-f", "owner=" + parts[0],
            "-f", "name=" + parts[1],
            "-F", "number=" + pullRequestNumber.ToString(CultureInfo.InvariantCulture));
        if (result.ExitCode != 0 || result.TimedOut)
            return new MergeQueueEntryState(null, null, null,
                "Could not query GitHub GraphQL mergeQueueEntry. " + AgentTaskGitOperations.Clip(result.StdErr.Trim()));
        var parsed = ParseMergeQueueEntryResponse(result.StdOut);
        return parsed.State ?? new MergeQueueEntryState(null, null, null, parsed.Error);
    }

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
        string? expectedTreeSha,
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
                || !GitHubLandingMergeExecutor.IsPostMergeTreeAcceptable(expectedTreeSha, tree.StdOut.Trim())
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

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
