namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Globalization;
using System.Text.Json;
using Domain.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

internal static class AgentTaskGitHubPullRequestOperations
{
    private sealed record PullRequestView(
        string State,
        bool IsDraft,
        string BaseBranch,
        string HeadSha,
        string Mergeable,
        string MergeStateStatus,
        string ReviewDecision,
        string Url,
        string? MergedAt,
        string? MergeCommitSha,
        bool HasAutoMergeRequest);

    private sealed record RequiredCheck(
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
        var allowedArguments = new HashSet<string>(StringComparer.Ordinal)
            { "action", "method" };
        if (args.Any(pair => !allowedArguments.Contains(pair.Key)))
            return AgentTaskGitOperations.Error("git_pr accepts only action and method.");

        var action = ReadString(args, "action");
        if (action is not ("status" or "ready" or "merge"))
            return AgentTaskGitOperations.Error("git_pr action must be status, ready, or merge.");

        if (action == "merge"
            && !string.Equals(pipelineRole, "Review", StringComparison.OrdinalIgnoreCase))
            return AgentTaskGitOperations.Error("Only the independent Review role may merge a task pull request.");

        var method = "merge";
        if (args.ContainsKey("method"))
        {
            method = ReadString(args, "method");
            if (method is not ("merge" or "squash" or "rebase"))
                return AgentTaskGitOperations.Error("git_pr method must be merge, squash, or rebase.");
            if (action != "merge")
                return AgentTaskGitOperations.Error("git_pr method is available only with action=merge.");
        }

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
        var headMatches = string.Equals(view.HeadSha, localHeadSha, StringComparison.OrdinalIgnoreCase);
        var requiredChecksPassed = checkList.Count > 0
            && checkList.All(check => string.Equals(check.Bucket, "pass", StringComparison.OrdinalIgnoreCase));
        var canMerge = view.State == "OPEN"
            && !view.IsDraft
            && headMatches
            && baseUnchanged
            && worktreeClean
            && string.Equals(view.Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase)
            && string.Equals(view.MergeStateStatus, "CLEAN", StringComparison.OrdinalIgnoreCase)
            && string.Equals(view.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)
            && requiredChecksPassed;

        if (action == "status")
            return AgentTaskGitOperations.Success(BuildStatus(
                repository, context, localHeadSha, remoteBaseSha!, worktreeClean,
                matching, view, checkList, headMatches, baseUnchanged, requiredChecksPassed, canMerge));

        if (action == "ready")
        {
            if (view.State != "OPEN")
                return AgentTaskGitOperations.Error("Only an open task pull request can be marked ready.");
            if (!view.IsDraft)
                return AgentTaskGitOperations.Success(new
                {
                    ready = true,
                    alreadyReady = true,
                    status = BuildStatus(
                        repository, context, localHeadSha, remoteBaseSha!, worktreeClean,
                        matching, view, checkList, headMatches, baseUnchanged, requiredChecksPassed, canMerge)
                });
            if (!worktreeClean || !headMatches || !baseUnchanged)
                return AgentTaskGitOperations.Error(
                    "The task branch must be clean, pushed at the current local HEAD, and based on the unchanged review-base before marking its PR ready. Run git_commit/git_integrate, review again, and republish.");

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
                return AgentTaskGitOperations.Error("The PR was marked ready, but its status could not be re-read: " + refreshed.Error);
            return AgentTaskGitOperations.Success(new
            {
                ready = !refreshed.View!.IsDraft,
                pullRequestNumber = matching.Number,
                pullRequestUrl = refreshed.View.Url,
                headSha = refreshed.View.HeadSha,
                baseBranch = refreshed.View.BaseBranch
            });
        }

        if (view.State == "MERGED")
            return AgentTaskGitOperations.Success(new
            {
                merged = true,
                alreadyMerged = true,
                pullRequestNumber = matching.Number,
                pullRequestUrl = view.Url,
                mergeCommitSha = view.MergeCommitSha,
                mergedAt = view.MergedAt
            });

        var blockers = GetMergeBlockers(
            context, localHeadSha, worktreeClean, remoteBaseSha!, matching, view,
            headMatches, baseUnchanged, checkList, requiredChecksPassed);
        if (blockers.Count > 0)
            return AgentTaskGitOperations.Error(
                "GitHub PR merge refused by ProjectBeacon preflight: " + string.Join("; ", blockers));

        var allowedMethod = await IsMergeMethodAllowedAsync(
            ghPath, context.WorktreeRoot, repository, method, ct);
        if (allowedMethod.Error is not null)
            return AgentTaskGitOperations.Error(allowedMethod.Error);
        if (!allowedMethod.Allowed)
            return AgentTaskGitOperations.Error(
                $"Repository settings do not allow the requested '{method}' merge method.");

        var mergeArguments = new List<string>
        {
            "pr", "merge", matching.Number.ToString(CultureInfo.InvariantCulture),
            "--repo", repository,
            "--match-head-commit", view.HeadSha
        };
        mergeArguments.Add(method switch
        {
            "merge" => "--merge",
            "squash" => "--squash",
            "rebase" => "--rebase",
            _ => throw new InvalidOperationException("Validated merge method changed unexpectedly.")
        });

        var merge = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, context.WorktreeRoot, ct, [.. mergeArguments]);
        if (merge.ExitCode != 0 || merge.TimedOut)
            return AgentTaskGitOperations.Error(
                "GitHub refused the PR merge. Branch protection and merge-queue rules were not bypassed. "
                + AgentTaskGitOperations.Clip(merge.StdErr.Trim()));

        var after = await ReadSnapshotAsync(ghPath, context.WorktreeRoot, repository, matching.Number, ct);
        if (after.Error is not null)
            return AgentTaskGitOperations.Error(
                "GitHub accepted the merge request, but the final PR state could not be verified: " + after.Error);

        if (after.View!.State == "MERGED")
        {
            if (string.IsNullOrWhiteSpace(after.View.MergeCommitSha) || string.IsNullOrWhiteSpace(after.View.MergedAt))
                return AgentTaskGitOperations.Error(
                    "GitHub reports the PR merged, but returned incomplete merge commit evidence.");
            return AgentTaskGitOperations.Success(new
            {
                merged = true,
                pullRequestNumber = matching.Number,
                pullRequestUrl = after.View.Url,
                mergeCommitSha = after.View.MergeCommitSha,
                mergedAt = after.View.MergedAt,
                method
            });
        }

        return AgentTaskGitOperations.Success(new
        {
            merged = false,
            mergeRequested = true,
            mergeQueued = merge.StdOut.Contains("queue", StringComparison.OrdinalIgnoreCase)
                || merge.StdErr.Contains("queue", StringComparison.OrdinalIgnoreCase),
            pullRequestNumber = matching.Number,
            pullRequestUrl = after.View.Url,
            state = after.View.State,
            mergeStateStatus = after.View.MergeStateStatus,
            message = "GitHub has not yet confirmed the PR as merged. Query git_pr action=status to track the final state."
        });
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
        bool canMerge) => new
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
        canMerge
    };

    private static List<string> GetMergeBlockers(
        AgentTaskGitOperations.GitContext context,
        string localHeadSha,
        bool worktreeClean,
        string remoteBaseSha,
        AgentTaskGitOperations.PullRequestInfo listed,
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
            blockers.Add("GitHub reports no required status checks; refusing an unverified merge");
        else if (!requiredChecksPassed)
            blockers.Add("one or more required status checks are not passing");
        return blockers;
    }

    private static async Task<(PullRequestView? View, string? Error)> ReadSnapshotAsync(
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
            "--json", "state,isDraft,mergeable,mergeStateStatus,reviewDecision,headRefOid,baseRefName,url,mergedAt,mergeCommit,autoMergeRequest");
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

            var mergedAt = JsonStringOrNull(root, "mergedAt");
            var autoMerge = root.TryGetProperty("autoMergeRequest", out var autoMergeValue)
                && autoMergeValue.ValueKind == JsonValueKind.Object;
            return (new PullRequestView(
                state,
                draftValue.GetBoolean(),
                baseBranch,
                headSha,
                JsonString(root, "mergeable"),
                JsonString(root, "mergeStateStatus"),
                JsonString(root, "reviewDecision"),
                url,
                mergedAt,
                mergeCommitSha,
                autoMerge), null);
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
        if (result.TimedOut || string.IsNullOrWhiteSpace(result.StdOut))
            return (null, "Could not read required GitHub status checks. " + AgentTaskGitOperations.Clip(result.StdErr.Trim()));

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
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

    private static async Task<(bool Allowed, string? Error)> IsMergeMethodAllowedAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        string method,
        CancellationToken ct)
    {
        var result = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "api", "repos/" + repository,
            "--jq", "{allow_merge_commit,allow_squash_merge,allow_rebase_merge}");
        if (result.ExitCode != 0 || result.TimedOut)
            return (false, "Could not read repository merge-method settings. " + AgentTaskGitOperations.Clip(result.StdErr.Trim()));
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var propertyName = method switch
            {
                "merge" => "allow_merge_commit",
                "squash" => "allow_squash_merge",
                "rebase" => "allow_rebase_merge",
                _ => string.Empty
            };
            return (document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.True, null);
        }
        catch (JsonException)
        {
            return (false, "GitHub returned malformed repository merge settings.");
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
