namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using System.Text.Json;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Server-authorized asynchronous GitHub landing for a Beacon GitLand Execute command.
/// Repository, PR, task branch and review base are derived from the pinned worktree.
/// GitHub evaluates branch and repository rules when the queued or direct merge is executed,
/// not when the asynchronous merge request is first accepted.
/// </summary>
internal static class GitHubLandingMergeExecutor
{
    internal sealed record MergeResult(
        string Repository,
        int PullRequestNumber,
        string PullRequestUrl,
        string SourceSha,
        string TargetBranch,
        string MergeCommitSha,
        string RemoteBaseSha,
        DateTime MergedAtUtc);

    internal sealed record MergeExecution(
        MergeResult? Result,
        string? ErrorCode,
        bool CompletionUnknown,
        string? MergeRequestId = null,
        string? MergeRequestStatus = null);

    public static async Task<MergeExecution> MergeAsync(
        AgentTaskGitOperations.GitContext context,
        string expectedTargetBranch,
        string expectedSourceSha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.BaseBranch)
            || !string.Equals(context.BaseBranch, expectedTargetBranch, StringComparison.Ordinal))
            return Failed("GitHubRemoteTargetMismatch");
        if (!AgentTaskGitOperations.IsGitSha(expectedSourceSha))
            return Failed("TaskSourceShaInvalid");

        var baseFormat = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", context.BaseBranch);
        var taskFormat = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", context.BranchName);
        if (!AgentTaskGitOperations.Succeeded(baseFormat)
            || !AgentTaskGitOperations.Succeeded(taskFormat))
            return Failed("GitHubLandingBranchInvalid");

        var origin = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "remote", "get-url", "--all", "origin");
        if (!AgentTaskGitOperations.Succeeded(origin))
            return Failed("GitHubOriginUnavailable");
        var originUrls = SplitLines(origin.StdOut);
        if (originUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(originUrls[0], out var repository))
            return Failed("GitHubOriginIdentityInvalid");

        var pushOrigin = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "remote", "get-url", "--push", "--all", "origin");
        if (!AgentTaskGitOperations.Succeeded(pushOrigin))
            return Failed("GitHubPushOriginUnavailable");
        var pushUrls = SplitLines(pushOrigin.StdOut);
        if (pushUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(pushUrls[0], out var pushRepository)
            || !string.Equals(repository, pushRepository, StringComparison.OrdinalIgnoreCase))
            return Failed("GitHubPushOriginIdentityMismatch");

        var mirror = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "config", "--bool", "--get", "remote.origin.mirror");
        if (mirror.TimedOut || mirror.ExitCode is not (0 or 1))
            return Failed("GitHubMirrorSettingUnavailable");
        if (mirror.ExitCode == 0
            && bool.TryParse(mirror.StdOut.Trim(), out var mirrorEnabled)
            && mirrorEnabled)
            return Failed("GitHubMirrorPushNotAllowed");

        var taskState = await ReadTaskStateAsync(context, ct);
        if (taskState.ErrorCode is not null)
            return Failed(taskState.ErrorCode);
        if (!taskState.Clean
            || !string.Equals(taskState.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return Failed("TaskBranchChangedAfterReview");

        var remoteBase = await ReadRemoteBaseAsync(context, originUrls[0], ct);
        if (remoteBase.Sha is null)
            return Failed(remoteBase.ErrorCode ?? "GitHubReviewBaseUnavailable");

        var safePath = NativeCommandRunner.BuildSafePath(
            Environment.GetEnvironmentVariable("PATH"), context.WorktreeRoot);
        var ghPath = WorkstationProbe.Which("gh", safePath);
        if (ghPath is null)
            return Failed("GitHubCliUnavailable");
        var auth = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return Failed("GitHubCliUnauthenticated");

        var listed = await AgentTaskGitOperations.ListPullRequestsAsync(
            ghPath, context.WorktreeRoot, repository, context.BranchName, ct);
        if (listed.Error is not null)
            return Failed("GitHubPullRequestLookupFailed");

        var differentBase = listed.PullRequests.FirstOrDefault(
            pull => !string.Equals(pull.BaseRefName, context.BaseBranch, StringComparison.Ordinal));
        if (differentBase is not null)
            return Failed("GitHubPullRequestBaseMismatch");

        var matching = listed.PullRequests
            .Where(pull => string.Equals(pull.BaseRefName, context.BaseBranch, StringComparison.Ordinal))
            .OrderByDescending(pull => pull.State == "OPEN")
            .ThenByDescending(pull => pull.Number)
            .ToArray();
        var openPullRequests = matching.Where(pull => pull.State == "OPEN").ToArray();
        if (openPullRequests.Length > 1)
            return Failed("GitHubDuplicateOpenPullRequests");
        if (openPullRequests.Length == 0)
        {
            var merged = matching.Where(pull => pull.State == "MERGED").ToArray();
            if (merged.Length == 1)
                return await VerifyAlreadyMergedAsync(
                    context, originUrls[0], repository, ghPath, merged[0], expectedSourceSha, ct);
            return Failed("GitHubOpenPullRequestNotFound");
        }

        // An open PR can be merged only against the exact immutable base pinned when
        // the task branch was created. A changed base requires integrate + Review again.
        if (!string.Equals(remoteBase.Sha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return Failed("GitHubReviewBaseAdvanced");

        var pullRequest = openPullRequests[0];
        var snapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (snapshot.Error is not null)
            return Failed("GitHubPullRequestStatusUnavailable");
        var checks = await AgentTaskGitHubPullRequestOperations.ReadRequiredChecksAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (checks.Error is not null)
            return Failed("GitHubRequiredChecksUnavailable");

        var before = snapshot.View!;
        var checkList = checks.Checks!;

        // Re-read PR, task worktree and base after the independent checks request.
        var confirmedSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (confirmedSnapshot.Error is not null
            || !AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(before, confirmedSnapshot.View!))
            return Failed("GitHubPullRequestChangedDuringPreflight");

        var confirmedTask = await ReadTaskStateAsync(context, ct);
        if (confirmedTask.ErrorCode is not null)
            return Failed(confirmedTask.ErrorCode);
        if (!confirmedTask.Clean
            || !string.Equals(confirmedTask.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return Failed("TaskBranchChangedDuringPreflight");

        var confirmedBase = await ReadRemoteBaseAsync(context, originUrls[0], ct);
        if (confirmedBase.Sha is null)
            return Failed(confirmedBase.ErrorCode ?? "GitHubReviewBaseUnavailable");
        if (!string.Equals(confirmedBase.Sha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return Failed("GitHubReviewBaseAdvanced");

        var finalSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (finalSnapshot.Error is not null
            || !AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(before, finalSnapshot.View!)
            || !string.Equals(finalSnapshot.View!.Url, pullRequest.Url, StringComparison.Ordinal))
            return Failed("GitHubPullRequestChangedDuringPreflight");

        // One final base read after the PR snapshot closes the asynchronous window
        // between status/check collection and the mutating GitHub API call.
        var finalBase = await ReadRemoteBaseAsync(context, originUrls[0], ct);
        if (finalBase.Sha is null)
            return Failed(finalBase.ErrorCode ?? "GitHubReviewBaseUnavailable");
        if (!string.Equals(finalBase.Sha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return Failed("GitHubReviewBaseAdvanced");
        var finalPrSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (finalPrSnapshot.Error is not null
            || !AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(finalSnapshot.View!, finalPrSnapshot.View!))
            return Failed("GitHubPullRequestChangedDuringPreflight");

        var view = finalPrSnapshot.View!;
        var headMatches = string.Equals(view.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase);
        var checksPassed = checkList.Count > 0
            && checkList.All(check => string.Equals(check.Bucket, "pass", StringComparison.OrdinalIgnoreCase));
        var blockers = AgentTaskGitHubPullRequestOperations.GetMergeBlockers(
            context,
            expectedSourceSha,
            true,
            confirmedBase.Sha,
            view,
            headMatches,
            true,
            checkList,
            checksPassed);
        if (blockers.Count > 0)
            return Failed("GitHubMergeNotAdmissible");

        // GitHub's asynchronous merge endpoint is the supported path for regular,
        // stacked and merge-queue PRs. Never bypass repository rules; an enqueued or
        // pending request remains ExecutePending until AutoSync proves the final merge.
        var merge = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct,
            BuildMergeApiArguments(repository, pullRequest.Number, expectedSourceSha));
        var asyncMerge = ParseAsyncMergeResponse(merge.StdOut);
        if (asyncMerge.Error is not null && !string.IsNullOrWhiteSpace(merge.StdErr))
        {
            // gh api may return the structured 409 response body on stderr. Preserve its
            // existing request UUID so an idempotent retry can join the in-flight merge.
            var errorResponse = ParseAsyncMergeResponse(merge.StdErr);
            if (errorResponse.Error is null)
                asyncMerge = errorResponse;
        }
        if (asyncMerge.Error is null && asyncMerge.Status is "pending" or "enqueued")
        {
            if (asyncMerge.Status == "pending"
                && (!Guid.TryParse(asyncMerge.MergeRequestId, out var pendingMergeRequestId)
                    || pendingMergeRequestId == Guid.Empty))
                return Unknown("GitHubMergeRequestIdentityMissing");
            var normalizedRequestId = Guid.TryParse(asyncMerge.MergeRequestId, out var mergeRequestId)
                && mergeRequestId != Guid.Empty
                    ? mergeRequestId.ToString("D")
                    : null;
            return new MergeExecution(
                null, null, false, normalizedRequestId, asyncMerge.Status);
        }
        if (asyncMerge.Error is null && asyncMerge.Status == "failed")
            return Failed("GitHubAsyncMergeRejected");
        if (asyncMerge.Error is not null || asyncMerge.Status != "merged"
            || !AgentTaskGitOperations.IsGitSha(asyncMerge.MergeCommitSha ?? string.Empty))
        {
            // A lost response can occur after GitHub accepted the request. Re-read the PR
            // before deciding whether the outcome is a definite rejection or unknown.
            var afterFailure = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
            if (afterFailure.Error is not null)
                return Unknown("GitHubMergeOutcomeUnknown");
            if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                    afterFailure.View!, "MERGED", context.BaseBranch, expectedSourceSha))
                return merge.TimedOut || merge.ExitCode != 0
                    ? Unknown("GitHubMergeOutcomeUnknown")
                    : Failed("GitHubAsyncMergeRejected");
            if (!AgentTaskGitOperations.IsGitSha(afterFailure.View!.MergeCommitSha ?? string.Empty))
                return Unknown("GitHubMergeEvidenceInvalid");
        }

        var mergedSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (mergedSnapshot.Error is not null)
            return Unknown("GitHubMergeOutcomeUnknown");

        var mergedView = mergedSnapshot.View!;
        if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                mergedView, "MERGED", context.BaseBranch, expectedSourceSha)
            || !string.Equals(mergedView.Url, pullRequest.Url, StringComparison.Ordinal)
            || !AgentTaskGitOperations.IsGitSha(mergedView.MergeCommitSha ?? string.Empty)
            || !DateTimeOffset.TryParse(
                mergedView.MergedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var mergedAt))
            return Unknown("GitHubMergeEvidenceInvalid");

        if (asyncMerge.MergeCommitSha is not null
            && !string.Equals(asyncMerge.MergeCommitSha, mergedView.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            return Unknown("GitHubMergeResponseMismatch");

        var fetchedBase = await FetchAndVerifyMergeAsync(
            context, originUrls[0], mergedView.MergeCommitSha!, ct);
        if (fetchedBase.Sha is null)
            return Unknown(fetchedBase.ErrorCode ?? "GitHubMergedCommitNotReachable");
        var pinnedBaseReachable = await AgentTaskGitOperations.Git(
            context.RepositoryRoot, context.DisabledFilters, ct,
            "merge-base", "--is-ancestor", context.BaseSha, fetchedBase.Sha);
        if (!AgentTaskGitOperations.Succeeded(pinnedBaseReachable))
            return Unknown("GitHubPinnedReviewBaseNotReachable");

        var finalTask = await ReadTaskStateAsync(context, ct);
        if (finalTask.ErrorCode is not null
            || !finalTask.Clean
            || !string.Equals(finalTask.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return Unknown("TaskBranchChangedAfterGitHubMerge");

        return new MergeExecution(
            new MergeResult(
                repository,
                pullRequest.Number,
                mergedView.Url,
                expectedSourceSha,
                context.BaseBranch,
                mergedView.MergeCommitSha!,
                fetchedBase.Sha,
                mergedAt.UtcDateTime),
            null,
            false);
    }

    /// <summary>
    /// Validates the actual tree produced by a merge. A non-null candidate SHA means
    /// the merge is expected to reproduce a local GitLand candidate exactly. A null
    /// candidate is used only after a confirmed queued request, whose merge-group tree
    /// can legitimately include a newer base and earlier queued pull requests. Direct
    /// async responses with status=merged keep the strict candidate-tree comparison.
    /// </summary>
    internal static bool IsPostMergeTreeAcceptable(
        string? expectedCandidateTreeSha,
        string? actualMergeTreeSha) =>
        AgentTaskGitOperations.IsGitSha(actualMergeTreeSha)
        && (expectedCandidateTreeSha is null
            || AgentTaskGitOperations.IsGitSha(expectedCandidateTreeSha)
                && string.Equals(expectedCandidateTreeSha, actualMergeTreeSha, StringComparison.OrdinalIgnoreCase));

    internal static string[] BuildMergeApiArguments(
        string repository,
        int pullRequestNumber,
        string expectedHeadSha)
    {
        if (string.IsNullOrWhiteSpace(repository)
            || pullRequestNumber < 1
            || !AgentTaskGitOperations.IsGitSha(expectedHeadSha))
            throw new ArgumentException("A validated repository, PR number and exact head SHA are required.");

        return
        [
            "api",
            "--method", "PUT",
            $"repos/{repository}/pulls/{pullRequestNumber}/merge-async",
            "-f", "sha=" + expectedHeadSha,
            "-f", "merge_action=default"
        ];
    }

    internal sealed record AsyncMergeResponse(
        string? Status,
        string? MergeRequestId,
        string? MergeCommitSha,
        string? Error);

    internal static AsyncMergeResponse ParseAsyncMergeResponse(string json)
    {
        try
        {
            var payload = ExtractJsonObject(json);
            if (payload is null)
                return new AsyncMergeResponse(null, null, null, "GitHub asynchronous merge response did not contain JSON.");
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusValue)
                || statusValue.ValueKind != JsonValueKind.String)
                return new AsyncMergeResponse(null, null, null, "GitHub asynchronous merge response is missing status.");

            var status = statusValue.GetString()?.ToLowerInvariant();
            if (status is not ("pending" or "enqueued" or "merged" or "failed"))
                return new AsyncMergeResponse(status, null, null, "GitHub returned an unsupported asynchronous merge status.");

            var details = root.TryGetProperty("details", out var detailsValue)
                && detailsValue.ValueKind == JsonValueKind.Object
                    ? detailsValue
                    : default;
            var requestId = ReadJsonString(details, "uuid") ?? ReadJsonString(root, "uuid");
            var mergeSha = ReadJsonString(details, "sha") ?? ReadJsonString(root, "sha");
            return new AsyncMergeResponse(status, requestId, mergeSha, null);
        }
        catch (JsonException)
        {
            return new AsyncMergeResponse(null, null, null, "GitHub returned malformed asynchronous merge JSON.");
        }
    }

    private static string? ExtractJsonObject(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        return start >= 0 && end > start
            ? output[start..(end + 1)]
            : null;
    }

    private static string? ReadJsonString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    internal static (string? MergeCommitSha, string? Error) ParseMergeApiResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("merged", out var merged)
                || merged.ValueKind != JsonValueKind.True)
                return (null, "GitHub did not confirm that the pull request was merged.");
            if (!root.TryGetProperty("sha", out var sha)
                || sha.ValueKind != JsonValueKind.String
                || !AgentTaskGitOperations.IsGitSha(sha.GetString() ?? string.Empty))
                return (null, "GitHub did not return a valid merge commit SHA.");
            return (sha.GetString(), null);
        }
        catch (JsonException)
        {
            return (null, "GitHub returned malformed merge response JSON.");
        }
    }

    private static async Task<MergeExecution> VerifyAlreadyMergedAsync(
        AgentTaskGitOperations.GitContext context,
        string remoteUrl,
        string repository,
        string ghPath,
        AgentTaskGitOperations.PullRequestInfo pullRequest,
        string expectedSourceSha,
        CancellationToken ct)
    {
        var snapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (snapshot.Error is not null
            || !AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                snapshot.View!, "MERGED", context.BaseBranch!, expectedSourceSha)
            || !AgentTaskGitOperations.IsGitSha(snapshot.View!.MergeCommitSha ?? string.Empty)
            || !DateTimeOffset.TryParse(
                snapshot.View.MergedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var mergedAt))
            return Unknown("GitHubMergeAlreadyExistsButCouldNotBeVerified");

        if (!string.Equals(snapshot.View.Url, pullRequest.Url, StringComparison.Ordinal))
            return Unknown("GitHubMergedPullRequestIdentityMismatch");
        var checks = await AgentTaskGitHubPullRequestOperations.ReadRequiredChecksAsync(
            ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
        if (checks.Error is not null || checks.Checks is not { Count: > 0 }
            || checks.Checks.Any(check => !string.Equals(check.Bucket, "pass", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(snapshot.View.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            return Unknown("GitHubMergedRequiredChecksNotVerified");

        var fetched = await FetchAndVerifyMergeAsync(
            context, remoteUrl, snapshot.View.MergeCommitSha!, ct);
        if (fetched.Sha is null)
            return Unknown(fetched.ErrorCode ?? "GitHubMergedCommitNotReachable");
        var pinnedBaseReachable = await AgentTaskGitOperations.Git(
            context.RepositoryRoot, context.DisabledFilters, ct,
            "merge-base", "--is-ancestor", context.BaseSha, fetched.Sha);
        if (!AgentTaskGitOperations.Succeeded(pinnedBaseReachable))
            return Unknown("GitHubPinnedReviewBaseNotReachable");
        var taskState = await ReadTaskStateAsync(context, ct);
        if (taskState.ErrorCode is not null || !taskState.Clean
            || !string.Equals(taskState.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return Unknown("TaskBranchChangedAfterGitHubMerge");

        return new MergeExecution(
            new MergeResult(repository, pullRequest.Number, snapshot.View.Url,
                expectedSourceSha, context.BaseBranch!, snapshot.View.MergeCommitSha!,
                fetched.Sha, mergedAt.UtcDateTime),
            null,
            false);
    }

    private static async Task<(string? Sha, string? ErrorCode)> ReadRemoteBaseAsync(
        AgentTaskGitOperations.GitContext context,
        string remoteUrl,
        CancellationToken ct)
    {
        var remote = await AgentTaskGitOperations.RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, remoteUrl,
            "ls-remote", "--heads", "--", remoteUrl, "refs/heads/" + context.BaseBranch);
        if (!AgentTaskGitOperations.Succeeded(remote))
            return (null, "GitHubReviewBaseUnavailable");
        var line = SplitLines(remote.StdOut)
            .FirstOrDefault(value => value.EndsWith(
                "\trefs/heads/" + context.BaseBranch, StringComparison.Ordinal));
        var sha = line?.Split('\t').FirstOrDefault();
        return AgentTaskGitOperations.IsGitSha(sha ?? string.Empty)
            ? (sha, null)
            : (null, "GitHubReviewBaseUnavailable");
    }

    private static async Task<(string? Sha, string? ErrorCode)> FetchAndVerifyMergeAsync(
        AgentTaskGitOperations.GitContext context,
        string remoteUrl,
        string mergeCommitSha,
        CancellationToken ct)
    {
        var fetch = await AgentTaskGitOperations.RunRemoteGitAsync(
            context.RepositoryRoot, context.DisabledFilters, ct, remoteUrl,
            "fetch", "--no-tags", "--no-recurse-submodules",
            "--upload-pack=git-upload-pack", "--", remoteUrl,
            $"+refs/heads/{context.BaseBranch}:refs/remotes/origin/{context.BaseBranch}");
        if (!AgentTaskGitOperations.Succeeded(fetch))
            return (null, "GitHubMergedTargetFetchFailed");

        var remoteHead = await AgentTaskGitOperations.Git(
            context.RepositoryRoot, context.DisabledFilters, ct,
            "rev-parse", "--verify", $"refs/remotes/origin/{context.BaseBranch}");
        if (!AgentTaskGitOperations.Succeeded(remoteHead)
            || !AgentTaskGitOperations.IsGitSha(remoteHead.StdOut.Trim()))
            return (null, "GitHubMergedTargetHeadUnavailable");

        var ancestor = await AgentTaskGitOperations.Git(
            context.RepositoryRoot, context.DisabledFilters, ct,
            "merge-base", "--is-ancestor", mergeCommitSha, remoteHead.StdOut.Trim());
        if (!AgentTaskGitOperations.Succeeded(ancestor))
            return (null, "GitHubMergedCommitNotReachable");

        return (remoteHead.StdOut.Trim(), null);
    }

    private static async Task<(bool Clean, string? HeadSha, string? ErrorCode)> ReadTaskStateAsync(
        AgentTaskGitOperations.GitContext context,
        CancellationToken ct)
    {
        var status = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "status", "--porcelain", "--untracked-files=all");
        var head = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!AgentTaskGitOperations.Succeeded(status)
            || !AgentTaskGitOperations.Succeeded(head)
            || !AgentTaskGitOperations.IsGitSha(head.StdOut.Trim()))
            return (false, null, "TaskBranchStatusUnavailable");
        return (string.IsNullOrWhiteSpace(status.StdOut), head.StdOut.Trim(), null);
    }

    private static MergeExecution Failed(string code) => new(null, code, false);
    private static MergeExecution Unknown(string code) => new(null, code, true);

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
