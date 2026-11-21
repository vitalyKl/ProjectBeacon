namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

internal static class AgentTaskGitHubPullRequestOperations
{
    internal sealed record PullRequestView(
        string State,
        bool IsDraft,
        string BaseBranch,
        string HeadSha,
        string Mergeable,
        string MergeStateStatus,
        string ReviewDecision,
        string Url,
        string? MergedAt,
        string? MergeCommitSha);

    internal sealed record RequiredCheck(
        string Name,
        string State,
        string Bucket,
        string Link,
        string Workflow);

    public static async Task<McpToolResult> ExecuteAsync(
        AgentTaskGitOperations.GitContext context,
        string? pipelineRole,
        JsonObject args,
        CancellationToken ct)
    {
        if (args.Any(pair => pair.Key != "action"))
            return AgentTaskGitOperations.Error("git_pr accepts only action.");

        var action = ReadString(args, "action");
        if (action is not ("status" or "ready"))
            return AgentTaskGitOperations.Error("git_pr action must be status or ready.");
        if (action == "ready" && !CanMarkReady(pipelineRole))
            return AgentTaskGitOperations.Error("Only the Actor role may mark its own task pull request ready.");

        if (string.IsNullOrWhiteSpace(context.BaseBranch))
            return AgentTaskGitOperations.Error("This task branch has no daemon-selected review base.");

        var validateBase = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", context.BaseBranch);
        if (!AgentTaskGitOperations.Succeeded(validateBase))
            return AgentTaskGitOperations.Error("The daemon-selected review base is not a valid Git branch.");

        var validateTaskBranch = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", context.BranchName);
        if (!AgentTaskGitOperations.Succeeded(validateTaskBranch))
            return AgentTaskGitOperations.Error("The daemon-authorized task branch is not a valid Git branch.");

        var origin = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "remote", "get-url", "--all", "origin");
        if (!AgentTaskGitOperations.Succeeded(origin))
            return AgentTaskGitOperations.Error("Could not inspect the configured GitHub origin.");
        var originUrls = SplitLines(origin.StdOut);
        if (originUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(originUrls[0], out var repository))
            return AgentTaskGitOperations.Error("GitHub PR operations require exactly one credential-free github.com origin URL.");

        var pushOrigin = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "remote", "get-url", "--push", "--all", "origin");
        if (!AgentTaskGitOperations.Succeeded(pushOrigin))
            return AgentTaskGitOperations.Error("Could not inspect the configured origin push URL.");
        var pushUrls = SplitLines(pushOrigin.StdOut);
        if (pushUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(pushUrls[0], out var pushRepository)
            || !string.Equals(repository, pushRepository, StringComparison.OrdinalIgnoreCase))
            return AgentTaskGitOperations.Error("The origin push URL must be one GitHub URL for the same repository as the validated fetch URL.");

        var mirror = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "config", "--bool", "--get", "remote.origin.mirror");
        if (mirror.TimedOut || mirror.ExitCode is not (0 or 1))
            return AgentTaskGitOperations.Error("Could not verify that origin is not configured for mirror pushes.");
        if (mirror.ExitCode == 0
            && bool.TryParse(mirror.StdOut.Trim(), out var mirrorEnabled)
            && mirrorEnabled)
            return AgentTaskGitOperations.Error("GitHub PR operations refuse remote.origin.mirror=true.");

        var status = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "status", "--porcelain", "--untracked-files=all");
        if (!AgentTaskGitOperations.Succeeded(status))
            return AgentTaskGitOperations.GitFailure("Could not inspect the task worktree before the GitHub PR operation.", status.StdErr);
        var worktreeClean = string.IsNullOrWhiteSpace(status.StdOut);

        var head = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!AgentTaskGitOperations.Succeeded(head) || !AgentTaskGitOperations.IsGitSha(head.StdOut.Trim()))
            return AgentTaskGitOperations.GitFailure("Could not resolve the task branch HEAD.", head.StdErr);
        var localHeadSha = head.StdOut.Trim();

        var remoteBase = await AgentTaskGitOperations.RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, originUrls[0],
            "ls-remote", "--heads", "--", originUrls[0], "refs/heads/" + context.BaseBranch);
        if (!AgentTaskGitOperations.Succeeded(remoteBase))
            return AgentTaskGitOperations.GitFailure("Could not verify the remote review-base branch.", remoteBase.StdErr);
        var baseLine = SplitLines(remoteBase.StdOut)
            .FirstOrDefault(line => line.EndsWith("\trefs/heads/" + context.BaseBranch, StringComparison.Ordinal));
        var remoteBaseSha = baseLine?.Split('\t').FirstOrDefault();
        if (!AgentTaskGitOperations.IsGitSha(remoteBaseSha ?? string.Empty))
            return AgentTaskGitOperations.Error("The configured GitHub origin does not contain the daemon-selected review base.");
        var baseUnchanged = string.Equals(remoteBaseSha, context.BaseSha, StringComparison.OrdinalIgnoreCase);

        var safePath = NativeCommandRunner.BuildSafePath(
            Environment.GetEnvironmentVariable("PATH"), context.WorktreeRoot);
        var ghPath = WorkstationProbe.Which("gh", safePath);
        if (ghPath is null)
            return AgentTaskGitOperations.Error("GitHub PR operations require GitHub CLI (gh) on this workstation.");
        var auth = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return AgentTaskGitOperations.Error("GitHub CLI is not authenticated for github.com. Run gh auth login on this workstation.");

        var existing = await AgentTaskGitOperations.ListPullRequestsAsync(
            ghPath, context.WorktreeRoot, repository, context.BranchName, ct);
        if (existing.Error is not null)
            return AgentTaskGitOperations.Error(existing.Error);

        var matching = existing.PullRequests
            .Where(pull => string.Equals(pull.BaseRefName, context.BaseBranch, StringComparison.Ordinal))
            .OrderByDescending(pull => pull.State == "OPEN")
            .ThenByDescending(pull => pull.State == "MERGED")
            .FirstOrDefault();
        var differentBase = existing.PullRequests.FirstOrDefault(
            pull => !string.Equals(pull.BaseRefName, context.BaseBranch, StringComparison.Ordinal));
        if (differentBase is not null)
            return AgentTaskGitOperations.Error(
                $"Task branch already has pull request #{differentBase.Number} targeting '{differentBase.BaseRefName}' ({differentBase.Url}); the daemon-selected review base is '{context.BaseBranch}'.");

        if (matching is null && action == "ready")
            return AgentTaskGitOperations.Error("No open pull request exists for this task branch and the daemon-selected review base. Publish the task branch first.");

        if (matching is null)
            return AgentTaskGitOperations.Success(new
            {
                found = false,
                repository,
                branch = context.BranchName,
                baseBranch = context.BaseBranch,
                headSha = localHeadSha,
                remoteBaseSha,
                baseUnchanged,
                worktreeClean
            });

        var snapshot = await ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (snapshot.Error is not null)
            return AgentTaskGitOperations.Error(snapshot.Error);

        var checks = await ReadRequiredChecksAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (checks.Error is not null)
            return AgentTaskGitOperations.Error(checks.Error);

        var view = snapshot.View!;
        var checkList = checks.Checks!;

        // gh pr checks resolves the PR's current head independently from gh pr view.
        // Re-read the PR after the checks request and refuse to combine data from
        // different heads or review states.
        var confirmedSnapshot = await ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (confirmedSnapshot.Error is not null)
            return AgentTaskGitOperations.Error(
                "Could not confirm the pull request remained stable while required checks were read. " + confirmedSnapshot.Error);
        var confirmedView = confirmedSnapshot.View!;
        if (!MatchesStatusSnapshot(view, confirmedView))
            return AgentTaskGitOperations.Error(
                "The pull request changed while its required checks were being read. Retry git_pr status to inspect one consistent head and review snapshot.");
        view = confirmedView;

        // Re-read local worktree and remote base after the network calls so status
        // and ready decisions use the latest observable task state, not the earlier
        // preflight values.
        var confirmedStatus = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "status", "--porcelain", "--untracked-files=all");
        if (!AgentTaskGitOperations.Succeeded(confirmedStatus))
            return AgentTaskGitOperations.GitFailure(
                "Could not revalidate the task worktree after reading GitHub checks.", confirmedStatus.StdErr);
        worktreeClean = string.IsNullOrWhiteSpace(confirmedStatus.StdOut);

        var confirmedHead = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!AgentTaskGitOperations.Succeeded(confirmedHead)
            || !AgentTaskGitOperations.IsGitSha(confirmedHead.StdOut.Trim()))
            return AgentTaskGitOperations.GitFailure(
                "Could not revalidate the task branch HEAD after reading GitHub checks.", confirmedHead.StdErr);
        localHeadSha = confirmedHead.StdOut.Trim();

        var confirmedRemoteBase = await AgentTaskGitOperations.RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, originUrls[0],
            "ls-remote", "--heads", "--", originUrls[0], "refs/heads/" + context.BaseBranch);
        if (!AgentTaskGitOperations.Succeeded(confirmedRemoteBase))
            return AgentTaskGitOperations.GitFailure(
                "Could not revalidate the remote review base after reading GitHub checks.", confirmedRemoteBase.StdErr);
        var confirmedBaseLine = SplitLines(confirmedRemoteBase.StdOut)
            .FirstOrDefault(line => line.EndsWith("\trefs/heads/" + context.BaseBranch, StringComparison.Ordinal));
        remoteBaseSha = confirmedBaseLine?.Split('\t').FirstOrDefault();
        if (!AgentTaskGitOperations.IsGitSha(remoteBaseSha ?? string.Empty))
            return AgentTaskGitOperations.Error(
                "The configured GitHub origin no longer exposes the daemon-selected review base.");
        baseUnchanged = string.Equals(remoteBaseSha, context.BaseSha, StringComparison.OrdinalIgnoreCase);

        // The local/base revalidation above is also an asynchronous window. Confirm
        // once more that the PR snapshot still matches the commit whose checks were read.
        var finalPreflightSnapshot = await ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (finalPreflightSnapshot.Error is not null)
            return AgentTaskGitOperations.Error(
                "Could not confirm the pull request after task-state revalidation. " + finalPreflightSnapshot.Error);
        if (!MatchesStatusSnapshot(view, finalPreflightSnapshot.View!))
            return AgentTaskGitOperations.Error(
                "The pull request changed during task-state revalidation. Retry git_pr status to inspect one consistent snapshot.");
        view = finalPreflightSnapshot.View!;

        if (!string.Equals(view.BaseBranch, context.BaseBranch, StringComparison.Ordinal))
            return AgentTaskGitOperations.Error(
                $"The task PR base changed from daemon-selected '{context.BaseBranch}' to '{view.BaseBranch}'. Retarget it explicitly and create fresh review proof.");
        var headMatches = string.Equals(view.HeadSha, localHeadSha, StringComparison.OrdinalIgnoreCase);
        var requiredChecksPassed = checkList.Count > 0
            && checkList.All(check => string.Equals(check.Bucket, "pass", StringComparison.OrdinalIgnoreCase));
        var blockers = GetMergeBlockers(
            context, localHeadSha, worktreeClean, remoteBaseSha!, view,
            headMatches, baseUnchanged, checkList, requiredChecksPassed);
        var githubMergeAdmissible = blockers.Count == 0;

        if (action == "status")
            return AgentTaskGitOperations.Success(BuildStatus(
                repository, context, localHeadSha, remoteBaseSha!, worktreeClean,
                matching, view, checkList, headMatches, baseUnchanged, requiredChecksPassed,
                githubMergeAdmissible, blockers));

        if (view.State != "OPEN")
            return AgentTaskGitOperations.Error("Only an open task pull request can be marked ready.");

        var readyBlockers = GetReadyBlockers(worktreeClean, headMatches, baseUnchanged);
        if (readyBlockers.Count > 0)
            return AgentTaskGitOperations.Error(
                "The task PR cannot be confirmed ready: " + string.Join("; ", readyBlockers) +
                ". Commit/integrate, review again, and republish.");

        if (!view.IsDraft)
            return AgentTaskGitOperations.Success(new
            {
                ready = true,
                alreadyReady = true,
                pullRequestNumber = matching.Number,
                pullRequestUrl = view.Url,
                headSha = view.HeadSha,
                baseBranch = view.BaseBranch
            });

        var ready = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct,
            "pr", "ready", matching.Number.ToString(CultureInfo.InvariantCulture),
            "--repo", repository);
        if (ready.ExitCode != 0 || ready.TimedOut)
            return AgentTaskGitOperations.Error(
                "GitHub could not mark the task PR ready. " + AgentTaskGitOperations.Clip(ready.StdErr.Trim()));

        var refreshed = await ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (refreshed.Error is not null)
            return AgentTaskGitOperations.Error(
                "The PR was marked ready, but its status could not be re-read: " + refreshed.Error);

        var readyView = refreshed.View!;
        var stillCurrent = !readyView.IsDraft
            && readyView.State == "OPEN"
            && string.Equals(readyView.BaseBranch, context.BaseBranch, StringComparison.Ordinal)
            && string.Equals(readyView.HeadSha, localHeadSha, StringComparison.OrdinalIgnoreCase);
        if (!stillCurrent)
            return AgentTaskGitOperations.Error(
                "GitHub marked the PR ready, but its final base/head/state does not match the locally reviewed task branch. Re-read git_pr status and do not proceed to landing.");

        var finalStatus = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "status", "--porcelain", "--untracked-files=all");
        var finalHead = await AgentTaskGitOperations.Git(
            context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!AgentTaskGitOperations.Succeeded(finalStatus)
            || !AgentTaskGitOperations.Succeeded(finalHead)
            || !AgentTaskGitOperations.IsGitSha(finalHead.StdOut.Trim())
            || !string.IsNullOrWhiteSpace(finalStatus.StdOut)
            || !string.Equals(finalHead.StdOut.Trim(), localHeadSha, StringComparison.OrdinalIgnoreCase))
            return AgentTaskGitOperations.Error(
                "GitHub marked the PR ready, but the local task worktree changed during the operation. Re-read git_pr status before proceeding.");

        var finalRemoteBase = await AgentTaskGitOperations.RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, originUrls[0],
            "ls-remote", "--heads", "--", originUrls[0], "refs/heads/" + context.BaseBranch);
        if (!AgentTaskGitOperations.Succeeded(finalRemoteBase))
            return AgentTaskGitOperations.GitFailure(
                "The PR was marked ready, but the final remote review-base check failed.", finalRemoteBase.StdErr);
        var finalBaseLine = SplitLines(finalRemoteBase.StdOut)
            .FirstOrDefault(line => line.EndsWith("\trefs/heads/" + context.BaseBranch, StringComparison.Ordinal));
        var finalBaseSha = finalBaseLine?.Split('\t').FirstOrDefault();
        if (!AgentTaskGitOperations.IsGitSha(finalBaseSha ?? string.Empty)
            || !string.Equals(finalBaseSha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return AgentTaskGitOperations.Error(
                "The PR was marked ready, but the pinned review base advanced during the operation. Integrate and review again.");

        var finalSnapshot = await ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (finalSnapshot.Error is not null)
            return AgentTaskGitOperations.Error(
                "The PR was marked ready, but its final state could not be confirmed: " + finalSnapshot.Error);
        if (!MatchesStatusSnapshot(readyView, finalSnapshot.View!))
            return AgentTaskGitOperations.Error(
                "The PR changed after it was marked ready. Re-read git_pr status before proceeding to landing.");

        return AgentTaskGitOperations.Success(new
        {
            ready = true,
            pullRequestNumber = matching.Number,
            pullRequestUrl = readyView.Url,
            headSha = readyView.HeadSha,
            baseBranch = readyView.BaseBranch
        });
    }

    internal static bool MatchesStatusSnapshot(
        PullRequestView before,
        PullRequestView after) =>
        string.Equals(before.State, after.State, StringComparison.Ordinal)
        && before.IsDraft == after.IsDraft
        && string.Equals(before.BaseBranch, after.BaseBranch, StringComparison.Ordinal)
        && string.Equals(before.HeadSha, after.HeadSha, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.Mergeable, after.Mergeable, StringComparison.Ordinal)
        && string.Equals(before.MergeStateStatus, after.MergeStateStatus, StringComparison.Ordinal)
        && string.Equals(before.ReviewDecision, after.ReviewDecision, StringComparison.Ordinal);

    internal static bool MatchesPublishedPullRequest(
        PullRequestView view,
        string expectedState,
        string expectedBaseBranch,
        string expectedHeadSha) =>
        string.Equals(view.State, expectedState, StringComparison.Ordinal)
        && string.Equals(view.BaseBranch, expectedBaseBranch, StringComparison.Ordinal)
        && string.Equals(view.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase);

    internal static bool CanMarkReady(string? pipelineRole) =>
        string.Equals(pipelineRole, "Actor", StringComparison.OrdinalIgnoreCase);

    internal static List<string> GetReadyBlockers(
        bool worktreeClean,
        bool headMatches,
        bool baseUnchanged)
    {
        var blockers = new List<string>();
        if (!worktreeClean)
            blockers.Add("task worktree has uncommitted changes");
        if (!headMatches)
            blockers.Add("PR head does not match the local task HEAD");
        if (!baseUnchanged)
            blockers.Add("review base advanced from the pinned base");
        return blockers;
    }

    private static object BuildStatus(
        string repository,
        AgentTaskGitOperations.GitContext context,
        string localHeadSha,
        string remoteBaseSha,
        bool worktreeClean,
        AgentTaskGitOperations.PullRequestInfo listed,
        PullRequestView view,
        IReadOnlyList<RequiredCheck> checks,
        bool headMatches,
        bool baseUnchanged,
        bool requiredChecksPassed,
        bool githubMergeAdmissible,
        IReadOnlyList<string> blockers) => new
        {
            found = true,
            repository,
            pullRequestNumber = listed.Number,
            pullRequestUrl = view.Url,
            state = view.State,
            draft = view.IsDraft,
            branch = context.BranchName,
            baseBranch = view.BaseBranch,
            localHeadSha,
            pullRequestHeadSha = view.HeadSha,
            remoteBaseSha,
            pinnedBaseSha = context.BaseSha,
            headMatchesLocal = headMatches,
            baseUnchanged,
            worktreeClean,
            mergeable = view.Mergeable,
            mergeStateStatus = view.MergeStateStatus,
            reviewDecision = view.ReviewDecision,
            requiredChecks = checks,
            requiredChecksPassed,
            mergeCommitSha = view.MergeCommitSha,
            mergedAt = view.MergedAt,
            githubMergeAdmissible,
            mergeBlockers = blockers
        };

    internal static List<string> GetMergeBlockers(
        AgentTaskGitOperations.GitContext context,
        string localHeadSha,
        bool worktreeClean,
        string remoteBaseSha,
        PullRequestView view,
        bool headMatches,
        bool baseUnchanged,
        IReadOnlyList<RequiredCheck> checks,
        bool requiredChecksPassed)
    {
        var blockers = new List<string>();
        if (view.State != "OPEN")
            blockers.Add("PR is not open");
        if (view.IsDraft)
            blockers.Add("PR is still a draft");
        if (!worktreeClean)
            blockers.Add("task worktree has uncommitted changes");
        if (!headMatches)
            blockers.Add($"PR head {view.HeadSha} does not match local task HEAD {localHeadSha}");
        if (!baseUnchanged)
            blockers.Add($"review base advanced from pinned {context.BaseSha} to {remoteBaseSha}; integrate and review again");
        if (!string.Equals(view.Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase))
            blockers.Add("GitHub does not report the PR as MERGEABLE");
        if (!string.Equals(view.MergeStateStatus, "CLEAN", StringComparison.OrdinalIgnoreCase))
            blockers.Add($"GitHub merge state is {view.MergeStateStatus}, not CLEAN");
        if (!string.Equals(view.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            blockers.Add($"GitHub review decision is {view.ReviewDecision}, not APPROVED");
        if (checks.Count == 0)
            blockers.Add("GitHub reports no required status checks");
        else if (!requiredChecksPassed)
            blockers.Add("one or more required status checks are not passing");
        return blockers;
    }

    internal static async Task<(PullRequestView? View, string? Error)> ReadSnapshotAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        int pullRequestNumber,
        CancellationToken ct)
    {
        var result = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "pr", "view", pullRequestNumber.ToString(CultureInfo.InvariantCulture),
            "--repo", repository,
            "--json", "state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,baseRefName,url,mergedAt,mergeCommit");
        if (result.ExitCode != 0 || result.TimedOut)
            return (null, "Could not read GitHub pull request status. " + AgentTaskGitOperations.Clip(result.StdErr.Trim()));

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var state = JsonString(root, "state").ToUpperInvariant();
            var baseBranch = JsonString(root, "baseRefName");
            var headSha = JsonString(root, "headRefOid");
            var url = JsonString(root, "url");
            var mergeCommitSha = root.TryGetProperty("mergeCommit", out var mergeCommit)
                && mergeCommit.ValueKind == JsonValueKind.Object
                ? JsonString(mergeCommit, "oid")
                : null;
            if (state.Length == 0 || baseBranch.Length == 0 || !AgentTaskGitOperations.IsGitSha(headSha)
                || url.Length == 0)
                return (null, "GitHub returned incomplete pull request identity or head metadata.");
            if (!root.TryGetProperty("isDraft", out var draftValue)
                || draftValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, "GitHub returned incomplete pull request draft metadata.");

            return (new PullRequestView(
                state,
                draftValue.GetBoolean(),
                baseBranch,
                headSha,
                JsonString(root, "mergeable"),
                JsonString(root, "mergeStateStatus"),
                JsonString(root, "reviewDecision"),
                url,
                JsonStringOrNull(root, "mergedAt"),
                mergeCommitSha), null);
        }
        catch (JsonException)
        {
            return (null, "GitHub returned malformed pull request status JSON.");
        }
    }

    private static async Task<(IReadOnlyList<RequiredCheck>? Checks, string? Error)> ReadRequiredChecksAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        int pullRequestNumber,
        CancellationToken ct)
    {
        var result = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "pr", "checks", pullRequestNumber.ToString(CultureInfo.InvariantCulture),
            "--repo", repository, "--required",
            "--json", "name,state,bucket,link,workflow");
        return ParseRequiredChecksResult(result.ExitCode, result.TimedOut, result.StdOut, result.StdErr);
    }

    internal static (IReadOnlyList<RequiredCheck>? Checks, string? Error) ParseRequiredChecksResult(
        int exitCode,
        bool timedOut,
        string standardOutput,
        string standardError)
    {
        // gh pr checks uses exit code 8 when checks are pending. Keep that JSON result
        // so the caller can report pending checks as blockers; every other non-zero code
        // means the status lookup itself failed and must not be treated as trustworthy.
        if (timedOut || exitCode is not (0 or 8) || string.IsNullOrWhiteSpace(standardOutput))
            return (null, "Could not read required GitHub status checks. " + AgentTaskGitOperations.Clip(standardError.Trim()));

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return (null, "GitHub returned a non-array required-check result.");
            var checks = new List<RequiredCheck>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = JsonString(item, "name");
                if (name.Length == 0)
                    return (null, "GitHub returned a required check without a name.");
                checks.Add(new RequiredCheck(
                    name,
                    JsonString(item, "state"),
                    JsonString(item, "bucket"),
                    JsonString(item, "link"),
                    JsonString(item, "workflow")));
            }
            return (checks, null);
        }
        catch (JsonException)
        {
            return (null, "GitHub returned malformed required-check JSON.");
        }
    }

    private static string[] SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

    private static string JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;

    private static string? JsonStringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static string ReadString(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
}
