namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using System.Text.Json;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Server-authorized GitHub landing for a Beacon GitLand Execute command.
/// Repository, PR, task branch and review base are derived from the pinned worktree;
/// the GitHub API still enforces repository branch protection and required reviews/checks.
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
        bool CompletionUnknown);

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
        if (!string.Equals(remoteBase.Sha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return Failed("GitHubReviewBaseAdvanced");

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
            || !AgentTaskGitHubPullRequestOperations.MatchesStatusSnapshot(before, finalSnapshot.View!))
            return Failed("GitHubPullRequestChangedDuringPreflight");

        var view = finalSnapshot.View!;
        var headMatches = string.Equals(view.HeadSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase);
        var checksPassed = checkList.Count > 0
            && checkList.All(check => string.Equals(check.Bucket, "pass", StringComparison.OrdinalIgnoreCase));
        var blockers = AgentTaskGitHubPullRequestOperations.GetMergeBlockers(
            context,
            expectedSourceSha,
            worktreeClean: true,
            confirmedBase.Sha,
            view,
            headMatches,
            baseUnchanged: true,
            checkList,
            checksPassed);
        if (blockers.Count > 0)
            return Failed("GitHubMergeNotAdmissible");

        var merge = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct,
            BuildMergeApiArguments(repository, pullRequest.Number, expectedSourceSha));
        if (merge.ExitCode != 0 || merge.TimedOut)
        {
            // A lost response can happen after GitHub accepted the merge. Re-read before
            // deciding whether this is a definite rejection or an unknown completion.
            var afterFailure = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                ghPath, context.WorktreeRoot, repository, pullRequest.Number, ct);
            if (afterFailure.Error is not null)
                return Unknown("GitHubMergeOutcomeUnknown");
            if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                    afterFailure.View!, "MERGED", context.BaseBranch, expectedSourceSha))
                return afterFailure.View!.State == "OPEN"
                    ? Failed("GitHubMergeRejected")
                    : Unknown("GitHubMergeOutcomeUnknown");
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

        if (merge.ExitCode == 0 && !merge.TimedOut)
        {
            var parsedMerge = ParseMergeApiResponse(merge.StdOut);
            if (parsedMerge.MergeCommitSha is null
                || !string.Equals(parsedMerge.MergeCommitSha, mergedView.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
                return Unknown("GitHubMergeResponseMismatch");
        }

        var fetchedBase = await FetchAndVerifyMergeAsync(
            context, originUrls[0], mergedView.MergeCommitSha!, ct);
        if (fetchedBase.Sha is null)
            return Unknown(fetchedBase.ErrorCode ?? "GitHubMergedCommitNotReachable");

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
            $"repos/{repository}/pulls/{pullRequestNumber}/merge",
            "-f", "sha=" + expectedHeadSha,
            "-f", "merge_method=merge"
        ];
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

        var fetched = await FetchAndVerifyMergeAsync(
            context, remoteUrl, snapshot.View.MergeCommitSha!, ct);
        if (fetched.Sha is null)
            return Unknown(fetched.ErrorCode ?? "GitHubMergedCommitNotReachable");

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
