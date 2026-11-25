namespace ProjectBeacon.Cli.Client;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Cli.Client.AgentHarness;

/// <summary>
/// Executes daemon-authorized Git landing operations. All repository, task branch, target branch,
/// and remote identity are derived from the persistent task worktree metadata.
/// </summary>
internal static class GitLandExecutor
{
    private const int MaxChangedFiles = 200;
    private const int MaxChangedLines = 20_000;
    private const string IntegrationBranchName = "beacon/integration";

    private sealed record Request(
        Guid ProjectId,
        Guid TaskId,
        Guid LandingAttemptId,
        string Action,
        string TargetBranch,
        string ReviewBaseBranch,
        bool AllowIntegrationFallback,
        string SourceSha,
        string ReviewTreeSha,
        string CheckCommandHash,
        string CheckCommand,
        string? TargetHeadSha,
        string? TargetTreeSha,
        string? MergeCommitSha,
        int MaxChangedFiles,
        int MaxChangedLines,
        IReadOnlyList<string> ProtectedPaths);

    private sealed record TargetCheckout(string Path, bool CheckedOut);
    private sealed record PostMergeCheckResult(int? ExitCode, string? OutputHash, string? Output, string? ErrorCode);
    private sealed record CandidateResult(
        string Status,
        string? TargetBranch = null,
        string? TargetHeadSha = null,
        string? CandidateTreeSha = null,
        string? CheckOutputHash = null,
        int? CheckExitCode = null,
        string? CheckOutput = null,
        IReadOnlyList<string>? Conflicts = null,
        string? ErrorCode = null);

    public static async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
        string projectRoot,
        Guid authorizedProjectId,
        string payloadJson,
        WorkstationSettings settings,
        CancellationToken ct)
    {
        if (!WorkstationWorkspaceMode.UsesTaskBranches(settings.WorkspaceMode))
            return BusinessFailure("WorkspaceModeRequiresTaskBranches");
        if (authorizedProjectId == Guid.Empty || !Directory.Exists(projectRoot))
            return BusinessFailure("ProjectRuntimeUnavailable");

        if (!TryReadRequest(payloadJson, out var request, out var parseError))
            return BusinessFailure("GitLandPayloadInvalid");
        if (request!.ProjectId != authorizedProjectId)
            return BusinessFailure("ProjectIdentityMismatch");

        if (request.Action is not "Revert"
            && !string.Equals(HashCommand(request.CheckCommand), request.CheckCommandHash, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("ReviewCommandHashMismatch");

        // The task worktree lease serializes operations for this task. Different task
        // branches use independent refs; shared target refs are guarded by update-ref CAS.
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, request.ProjectId, request.TaskId, settings.WorktreeRoot, ct);
        if (opened.Worktree is null)
            return BusinessFailure("TaskWorkspaceUnavailable");

        await using var taskWorkspace = opened.Worktree;
        var expectedBranch = $"beacon/task/{request.TaskId:N}";
        if (!string.Equals(taskWorkspace.BranchName, expectedBranch, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(taskWorkspace.BaseBranch))
            return BusinessFailure("TaskBranchIdentityUnavailable");
        if (!string.Equals(taskWorkspace.BaseBranch, request.ReviewBaseBranch, StringComparison.Ordinal))
            return BusinessFailure("ReviewBaseBranchIdentityMismatch");
        if (!string.Equals(request.TargetBranch, request.ReviewBaseBranch, StringComparison.Ordinal)
            && !string.Equals(request.TargetBranch, IntegrationBranchName, StringComparison.Ordinal))
            return BusinessFailure("TargetBranchIdentityMismatch");

        var targetBranchCheck = await Git(taskWorkspace.RepositoryRoot, [], ct, "check-ref-format", "--branch", request.TargetBranch);
        if (!Succeeded(targetBranchCheck))
            return BusinessFailure("TargetBranchInvalid");

        var repositoryRoot = taskWorkspace.RepositoryRoot;
        var filters = await ReadDisabledFiltersAsync(repositoryRoot, ct);
        if (filters.Error is not null)
            return BusinessFailure("GitFilterConfigurationUnavailable");
        var disabledFilters = filters.Filters!;

        var isGitHubOrigin = false;
        if (request.Action is "Execute" or "Revert")
        {
            var origin = await Git(
                repositoryRoot, disabledFilters, ct, "remote", "get-url", "--all", "origin");
            if (origin.TimedOut)
                return BusinessFailure("GitRemoteInspectionFailed");
            if (origin.ExitCode == 0)
            {
                var originUrls = origin.StdOut.Split(
                    ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                isGitHubOrigin = AgentTaskGitOperations.HasGitHubOrigin(originUrls);
                // Reverting a GitHub merge requires a new reviewed PR. Never mutate a
                // protected remote target through the local GitLand ref-revert path.
                if (isGitHubOrigin && request.Action == "Revert")
                    return BusinessFailure("GitHubRemoteRequiresPullRequest");
            }
            else if (origin.ExitCode != 2)
            {
                return BusinessFailure("GitRemoteInspectionFailed");
            }
        }

        var version = await Git(repositoryRoot, disabledFilters, ct, "--version");
        if (!Succeeded(version) || !AgentTaskWorktree.SupportsBranchWorkflow(version.StdOut))
            return BusinessFailure("GitVersionUnsupported");

        var branch = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!Succeeded(branch) || !string.Equals(branch.StdOut.Trim(), expectedBranch, StringComparison.Ordinal))
            return BusinessFailure("TaskBranchIdentityMismatch");

        if (request.Action is not "Revert")
        {
            var sourceHead = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(sourceHead) || !string.Equals(sourceHead.StdOut.Trim(), request.SourceSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("TaskSourceChangedSinceReview");

            var sourceTree = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "rev-parse", "--verify", request.SourceSha + "^{tree}");
            if (!Succeeded(sourceTree)
                || !string.Equals(sourceTree.StdOut.Trim(), request.ReviewTreeSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("TaskTreeDoesNotMatchReview");

            var sourceStatus = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(sourceStatus))
                return BusinessFailure("TaskBranchStatusUnavailable");
            if (!string.IsNullOrWhiteSpace(sourceStatus.StdOut))
                return BusinessFailure("TaskBranchDirtyAfterReview");
        }

        var customMergeDrivers = await Git(repositoryRoot, disabledFilters, ct, "config", "--get-regexp", "^merge\\..*\\.driver$");
        if (customMergeDrivers.TimedOut || customMergeDrivers.ExitCode is not (0 or 1))
            return BusinessFailure("MergeDriverInspectionFailed");
        if (customMergeDrivers.ExitCode == 0 && !string.IsNullOrWhiteSpace(customMergeDrivers.StdOut))
            return BusinessFailure("CustomMergeDriversConfigured");

        var outcome = request.Action == "Execute" && isGitHubOrigin
            ? await ExecuteGitHubLandingAsync(taskWorkspace, request, disabledFilters, ct)
            : request.Action switch
            {
                "Preview" => await PreviewAsync(taskWorkspace, request, disabledFilters, ct),
                "Execute" => await ExecuteLandingAsync(taskWorkspace, request, disabledFilters, ct),
                "Revert" => await RevertAsync(taskWorkspace, request, disabledFilters, ct),
                _ => (false, null, "GitLand action must be Preview, Execute, or Revert.")
            };

        // Retention starts only after the executor itself confirms a landed result. A
        // post-merge failure (landed_unverified), a retryable/unknown outcome, or a revert
        // never makes this task branch eligible for automatic cleanup.
        if (request.Action == "Execute"
            && TryReadSuccessfulLanding(outcome.Result, out var landedCommitSha))
        {
            var recorded = await taskWorkspace.MarkLandedAsync(
                request.TargetBranch, request.SourceSha, landedCommitSha!, DateTime.UtcNow, CancellationToken.None);
            if (!recorded.Success && !string.IsNullOrWhiteSpace(outcome.Result))
            {
                try
                {
                    var resultNode = JsonNode.Parse(outcome.Result) as JsonObject;
                    if (resultNode is not null)
                    {
                        resultNode["retentionRecorded"] = false;
                        resultNode["retentionWarning"] = recorded.Error;
                        outcome = (outcome.Ok, resultNode.ToJsonString(), outcome.Error);
                    }
                }
                catch (JsonException)
                {
                    // The landing result remains authoritative; the task is simply retained.
                }
            }
        }

        return outcome;
    }

    private static bool TryReadSuccessfulLanding(string? resultJson, out string? mergeCommitSha)
    {
        mergeCommitSha = null;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !string.Equals(status.GetString(), "landed", StringComparison.Ordinal)
                || !root.TryGetProperty("mergeCommitSha", out var merge)
                || merge.ValueKind != JsonValueKind.String
                || !IsGitSha(merge.GetString()))
                return false;
            mergeCommitSha = merge.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<(bool Ok, string? Result, string? Error)> PreviewAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        var candidate = await BuildCandidateAsync(workspace, request, filters, ct);
        return candidate.Status switch
        {
            "previewed" => (true, JsonSerializer.Serialize(new
            {
                status = "previewed",
                targetBranch = candidate.TargetBranch ?? request.TargetBranch,
                targetHeadSha = candidate.TargetHeadSha,
                candidateTreeSha = candidate.CandidateTreeSha,
                checkOutputHash = candidate.CheckOutputHash,
                checkExitCode = candidate.CheckExitCode,
                checkOutput = candidate.CheckOutput
            }), null),
            "conflicted" => (true, JsonSerializer.Serialize(new
            {
                status = "conflicted",
                targetBranch = candidate.TargetBranch ?? request.TargetBranch,
                targetHeadSha = candidate.TargetHeadSha,
                conflicts = candidate.Conflicts
            }), null),
            _ => (true, JsonSerializer.Serialize(new
            {
                status = "failed",
                targetBranch = candidate.TargetBranch ?? request.TargetBranch,
                targetHeadSha = candidate.TargetHeadSha,
                errorCode = candidate.ErrorCode ?? candidate.Status,
                checkExitCode = candidate.CheckExitCode,
                checkOutput = candidate.CheckOutput
            }), null)
        };
    }

    private static async Task<(bool Ok, string? Result, string? Error)> ExecuteGitHubLandingAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        if (!string.Equals(request.TargetBranch, request.ReviewBaseBranch, StringComparison.Ordinal))
            return BusinessFailure("GitHubRemoteTargetMismatch");
        if (!IsGitSha(request.TargetHeadSha) || !IsGitSha(request.TargetTreeSha))
            return BusinessFailure("ExecutePreviewEvidenceMissing");

        // Rebuild the exact candidate and rerun ReviewCheck before asking GitHub to
        // merge. A server-authorized Execute can never rely on stale preview evidence.
        var candidate = await BuildCandidateAsync(workspace, request, filters, ct);
        if (candidate.Status != "previewed")
            return BusinessFailure(candidate.ErrorCode ?? "CandidateNoLongerValid");
        if (!string.Equals(candidate.TargetBranch, request.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(candidate.TargetHeadSha, request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.CandidateTreeSha, request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("CandidateChangedAfterPreview");

        var projectRelativePath = Path.GetRelativePath(workspace.RecoveryPath, workspace.ProjectRoot)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var context = new AgentTaskGitOperations.GitContext(
            request.TaskId,
            workspace.RepositoryRoot,
            workspace.RecoveryPath,
            workspace.ProjectRoot,
            projectRelativePath == "." ? string.Empty : projectRelativePath,
            workspace.BranchName,
            workspace.BaseBranch,
            workspace.BaseSha,
            workspace.RecoveryPath + ".beacon.json",
            filters);

        var merge = await GitHubLandingMergeExecutor.MergeAsync(
            context, request.TargetBranch, request.SourceSha, ct);
        if (merge.CompletionUnknown)
            return ExecuteUnknown(request, null, merge.ErrorCode ?? "GitHubMergeOutcomeUnknown");
        if (merge.ErrorCode is not null || merge.Result is null)
            return BusinessFailure(merge.ErrorCode ?? "GitHubMergeFailed");

        // The GitHub merge commit is accepted only when its immutable tree is exactly
        // the tree reviewed by Beacon and the post-merge check still passes.
        var mergeTree = await Git(
            workspace.RepositoryRoot, filters, CancellationToken.None,
            "rev-parse", "--verify", merge.Result.MergeCommitSha + "^{tree}");
        if (!Succeeded(mergeTree)
            || !string.Equals(mergeTree.StdOut.Trim(), request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
        {
            return LandedUnverified(
                request, merge.Result.MergeCommitSha, null, null, "GitHubMergeTreeMismatch");
        }

        var postMerge = await RunPostMergeCheckAsync(
            workspace, request, merge.Result.MergeCommitSha, request.TargetTreeSha!,
            filters, CancellationToken.None);
        if (postMerge.ErrorCode is not null || postMerge.ExitCode != 0
            || !IsSha256(postMerge.OutputHash))
        {
            return LandedUnverified(
                request,
                merge.Result.MergeCommitSha,
                postMerge.ExitCode,
                postMerge.OutputHash,
                postMerge.ErrorCode ?? "PostMergeCheckFailed");
        }

        return (true, JsonSerializer.Serialize(new
        {
            status = "landed",
            targetBranch = request.TargetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = request.TargetTreeSha,
            mergeCommitSha = merge.Result.MergeCommitSha,
            postMergeCheckExitCode = postMerge.ExitCode,
            postMergeCheckOutputHash = postMerge.OutputHash,
            githubRepository = merge.Result.Repository,
            pullRequestNumber = merge.Result.PullRequestNumber,
            pullRequestUrl = merge.Result.PullRequestUrl,
            remoteBaseSha = merge.Result.RemoteBaseSha,
            mergedAtUtc = merge.Result.MergedAtUtc
        }), null);
    }

    private static (bool Ok, string? Result, string? Error) LandedUnverified(
        Request request,
        string mergeCommitSha,
        int? postMergeCheckExitCode,
        string? postMergeCheckOutputHash,
        string errorCode) =>
        (true, JsonSerializer.Serialize(new
        {
            status = "landed_unverified",
            targetBranch = request.TargetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = request.TargetTreeSha,
            mergeCommitSha,
            postMergeCheckExitCode,
            postMergeCheckOutputHash,
            errorCode
        }), null);

    private static async Task<(bool Ok, string? Result, string? Error)> ExecuteLandingAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        if (!IsGitSha(request.TargetHeadSha) || !IsGitSha(request.TargetTreeSha))
            return BusinessFailure("ExecutePreviewEvidenceMissing");

        var resumed = await TryResumeLandingAsync(workspace, request, filters, ct);
        if (resumed is { } resumedLanding)
            return await ReconcileLandingOutcomeAsync(workspace, request, filters, resumedLanding, CancellationToken.None);

        var candidate = await BuildCandidateAsync(workspace, request, filters, ct);
        if (candidate.Status != "previewed")
            return BusinessFailure(candidate.ErrorCode ?? "CandidateNoLongerValid");
        if (!string.Equals(candidate.TargetBranch, request.TargetBranch, StringComparison.Ordinal))
            return BusinessFailure("TargetBranchChangedSincePreview");
        if (!string.Equals(candidate.TargetHeadSha, request.TargetHeadSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("TargetChangedAfterPreview");
        if (!string.Equals(candidate.CandidateTreeSha, request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("CandidateTreeChangedAfterPreview");

        var repo = workspace.RepositoryRoot;
        var targetBranch = request.TargetBranch;
        var branchCheck = await Git(repo, filters, ct, "check-ref-format", "--branch", targetBranch);
        if (!Succeeded(branchCheck))
            return BusinessFailure("InvalidTargetBranch");

        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return BusinessFailure("TargetCheckoutUnavailable");
        if (checkout.Checkout!.CheckedOut
            && !IsAllowedTargetCheckout(checkout.Checkout, repo, request.LandingAttemptId))
            return BusinessFailure("TargetCheckedOutElsewhere");

        var branchRef = $"refs/heads/{targetBranch}";
        var localHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        var branchExists = Succeeded(localHead);
        if (!branchExists && localHead.ExitCode != 128)
            return BusinessFailure("TargetHeadUnavailable");
        if (checkout.Checkout.CheckedOut
            && (!branchExists
                || !string.Equals(localHead.StdOut.Trim(), request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)))
            return BusinessFailure("TargetChangedAfterPreview");
        if (!checkout.Checkout.CheckedOut && branchExists)
        {
            // A local target may lag behind the freshly fetched origin target. It is safe
            // only when it is an ancestor of the reviewed target; a local-ahead or diverged
            // branch must never be overwritten or silently discarded.
            if (await IsAncestorAsync(repo, filters, localHead.StdOut.Trim(), request.TargetHeadSha!, ct) != true)
                return BusinessFailure("LocalTargetDivergedFromReviewedOrigin");
        }

        if (checkout.Checkout.CheckedOut)
        {
            var clean = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            var currentBranch = await Git(checkout.Checkout.Path, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            var currentHead = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(clean) || !string.IsNullOrWhiteSpace(clean.StdOut)
                || !Succeeded(currentBranch)
                || !string.Equals(currentBranch.StdOut.Trim(), targetBranch, StringComparison.Ordinal)
                || !Succeeded(currentHead)
                || !string.Equals(currentHead.StdOut.Trim(), request.TargetHeadSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("TargetCheckoutChangedAfterPreview");
        }

        // Build the exact two-parent commit from the reviewed candidate tree.
        var commit = await Git(repo, filters, ct,
            "-c", "user.name=ProjectBeacon",
            "-c", "user.email=projectbeacon@localhost",
            "-c", "commit.gpgsign=false",
            "commit-tree", candidate.CandidateTreeSha!,
            "-p", request.TargetHeadSha!,
            "-p", request.SourceSha,
            "-m", $"ProjectBeacon: land task {request.TaskId:N}");
        if (!Succeeded(commit) || !IsGitSha(commit.StdOut.Trim()))
            return BusinessFailure("LandingCommitCreationFailed");
        var mergeCommitSha = commit.StdOut.Trim();

        var parents = await Git(repo, filters, ct, "rev-list", "--parents", "-n", "1", mergeCommitSha);
        var parentFields = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Succeeded(parents) || parentFields.Length != 3
            || !string.Equals(parentFields[1], request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parentFields[2], request.SourceSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("LandingCommitParentsInvalid");

        var mergeTree = await Git(repo, filters, ct, "rev-parse", "--verify", mergeCommitSha + "^{tree}");
        if (!Succeeded(mergeTree)
            || !string.Equals(mergeTree.StdOut.Trim(), candidate.CandidateTreeSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("LandingCommitTreeInvalid");

        // Persist the exact intended result before moving the target ref. If the daemon exits
        // after this point, a retry can validate this marker and finish the same CAS operation.
        var markerRef = LandingMarkerRef(request.LandingAttemptId, "merge");
        var markerCreate = await Git(repo, filters, CancellationToken.None,
            "update-ref", markerRef, mergeCommitSha, new string('0', mergeCommitSha.Length));
        if (!Succeeded(markerCreate))
        {
            var recovered = await TryResumeLandingAsync(workspace, request, filters, CancellationToken.None);
            if (recovered is { } recoveredResult)
                return await ReconcileLandingOutcomeAsync(workspace, request, filters, recoveredResult, CancellationToken.None);
            return ExecuteUnknown(request, null, "LandingMarkerCouldNotBePersisted");
        }

        // Persist the exact target ref value before changing it. This lets recovery validate
        // and refresh a deterministic target worktree even when the local branch lagged behind
        // the freshly reviewed origin HEAD.
        var targetBeforeRef = LandingMarkerRef(request.LandingAttemptId, "target-before");
        var targetBeforeSha = branchExists ? localHead.StdOut.Trim() : request.TargetHeadSha!;
        var existingTargetBefore = await Git(repo, filters, CancellationToken.None, "rev-parse", "--verify", targetBeforeRef);
        if (existingTargetBefore.ExitCode == 128 && !existingTargetBefore.TimedOut)
        {
            var createdTargetBefore = await Git(repo, filters, CancellationToken.None,
                "update-ref", targetBeforeRef, targetBeforeSha, new string('0', targetBeforeSha.Length));
            if (!Succeeded(createdTargetBefore))
                return BusinessFailure("LandingTargetBeforeMarkerCouldNotBePersisted");
        }
        else if (!Succeeded(existingTargetBefore)
            || !string.Equals(existingTargetBefore.StdOut.Trim(), targetBeforeSha, StringComparison.OrdinalIgnoreCase))
        {
            return BusinessFailure("LandingTargetBeforeMarkerMismatch");
        }

        // From the marker write onward, ignore caller cancellation: the target mutation must
        // either be reconciled and acknowledged or leave a durable attempt marker for retry.
        // The local branch may intentionally lag behind the freshly fetched target when
        // it is not checked out. We allowed that only after proving it is an ancestor of
        // the reviewed target, so compare-and-swap against the exact local value captured
        // before marker creation, not the remote preview HEAD.
        var expectedLocalHead = branchExists ? targetBeforeSha : new string('0', mergeCommitSha.Length);
        var update = await Git(
            repo, filters, CancellationToken.None,
            "update-ref", branchRef, mergeCommitSha, expectedLocalHead);
        if (!Succeeded(update))
        {
            var recovered = await TryResumeLandingAsync(workspace, request, filters, CancellationToken.None);
            if (recovered is { } recoveredResult)
                return await ReconcileLandingOutcomeAsync(workspace, request, filters, recoveredResult, CancellationToken.None);
            return ExecuteUnknown(request, mergeCommitSha, "TargetChangedBeforeLandingUpdate");
        }

        var finalRecovery = await TryResumeLandingAsync(workspace, request, filters, CancellationToken.None);
        if (finalRecovery is { } finalResult)
            return await ReconcileLandingOutcomeAsync(workspace, request, filters, finalResult, CancellationToken.None);
        return ExecuteUnknown(request, mergeCommitSha, "LandingMarkerCouldNotBeReconciled");
    }

    /// <summary>
    /// Once the merge marker exists, a recovery error must never be reported as an ordinary
    /// failure while the exact commit may already be reachable from the target branch.
    /// Verify the marker's parents/tree and the target ref before deciding whether retry is
    /// safe, the landing happened but needs attention, or the outcome is still unknown.
    /// </summary>
    private static async Task<(bool Ok, string? Result, string? Error)> ReconcileLandingOutcomeAsync(
        AgentTaskWorktree workspace,
        Request request,
        string[] filters,
        (bool Ok, string? Result, string? Error) result,
        CancellationToken ct)
    {
        if (!IsBusinessFailureResult(result))
            return result;

        var repo = workspace.RepositoryRoot;
        var markerRef = LandingMarkerRef(request.LandingAttemptId, "merge");
        var marker = await Git(repo, filters, ct, "rev-parse", "--verify", markerRef);
        if (!Succeeded(marker) || !IsGitSha(marker.StdOut.Trim()))
            return ExecuteUnknown(request, null, ReadFailureCode(result) ?? "LandingMarkerUnavailable");

        var mergeCommitSha = marker.StdOut.Trim();
        var parents = await Git(repo, filters, ct, "rev-list", "--parents", "-n", "1", mergeCommitSha);
        var parentFields = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mergeTree = await Git(repo, filters, ct, "rev-parse", "--verify", mergeCommitSha + "^{tree}");
        if (!Succeeded(parents) || parentFields.Length != 3
            || !string.Equals(parentFields[1], request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parentFields[2], request.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !Succeeded(mergeTree)
            || !string.Equals(mergeTree.StdOut.Trim(), request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
            return ExecuteUnknown(request, mergeCommitSha, "LandingMarkerDoesNotMatchPreview");

        var targetHead = await Git(repo, filters, ct, "rev-parse", "--verify", $"refs/heads/{request.TargetBranch}");
        if (!Succeeded(targetHead) || !IsGitSha(targetHead.StdOut.Trim()))
            return ExecuteUnknown(request, mergeCommitSha, ReadFailureCode(result) ?? "TargetHeadUnavailable");

        var containsMerge = await IsAncestorAsync(repo, filters, mergeCommitSha, targetHead.StdOut.Trim(), ct);
        if (containsMerge is null)
            return ExecuteUnknown(request, mergeCommitSha, "TargetReachabilityCouldNotBeVerified");
        if (!containsMerge.Value)
            return result;

        return (true, JsonSerializer.Serialize(new
        {
            status = "landed_unverified",
            targetBranch = request.TargetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = request.TargetTreeSha,
            mergeCommitSha,
            postMergeCheckExitCode = (int?)null,
            postMergeCheckOutputHash = (string?)null,
            errorCode = ReadFailureCode(result) ?? "LandingRecoveryFailed"
        }), null);
    }

    private static bool IsBusinessFailureResult((bool Ok, string? Result, string? Error) result)
    {
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Result))
            return false;
        try
        {
            using var document = JsonDocument.Parse(result.Result);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "failed", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadFailureCode((bool Ok, string? Result, string? Error) result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Result ?? "{}");
            return document.RootElement.TryGetProperty("errorCode", out var code)
                && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool Ok, string? Result, string? Error) ExecuteUnknown(
        Request request, string? mergeCommitSha, string errorCode) =>
        (true, JsonSerializer.Serialize(new
        {
            status = "execute_unknown",
            targetBranch = request.TargetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = request.TargetTreeSha,
            mergeCommitSha,
            errorCode
        }), null);

    private static async Task<(bool Ok, string? Result, string? Error)?> TryResumeLandingAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var markerRef = LandingMarkerRef(request.LandingAttemptId, "merge");
        var marker = await Git(repo, filters, ct, "rev-parse", "--verify", markerRef);
        if (marker.ExitCode == 128 && !marker.TimedOut)
            return null;
        if (!Succeeded(marker) || !IsGitSha(marker.StdOut.Trim()))
            return BusinessFailure("LandingMarkerInvalid");

        var mergeCommitSha = marker.StdOut.Trim();
        var parents = await Git(repo, filters, ct, "rev-list", "--parents", "-n", "1", mergeCommitSha);
        var parentFields = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tree = await Git(repo, filters, ct, "rev-parse", "--verify", mergeCommitSha + "^{tree}");
        if (!Succeeded(parents) || parentFields.Length != 3
            || !string.Equals(parentFields[1], request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parentFields[2], request.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !Succeeded(tree)
            || !string.Equals(tree.StdOut.Trim(), request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("LandingMarkerDoesNotMatchPreview");

        var targetBeforeRef = LandingMarkerRef(request.LandingAttemptId, "target-before");
        var targetBefore = await Git(repo, filters, ct, "rev-parse", "--verify", targetBeforeRef);
        var targetBeforeSha = Succeeded(targetBefore)
            ? targetBefore.StdOut.Trim()
            : targetBefore.ExitCode == 128 && !targetBefore.TimedOut
                ? request.TargetHeadSha
                : null;
        if (!IsGitSha(targetBeforeSha)
            || await IsAncestorAsync(repo, filters, targetBeforeSha!, request.TargetHeadSha!, ct) != true)
            return BusinessFailure("LandingTargetBeforeMarkerInvalid");

        var branchRef = $"refs/heads/{request.TargetBranch}";
        var branchHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        var branchExists = Succeeded(branchHead);
        if (!branchExists && branchHead.ExitCode != 128)
            return BusinessFailure("TargetHeadUnavailable");

        var checkout = await FindTargetCheckoutAsync(repo, request.TargetBranch, filters, ct);
        if (checkout.Error is not null)
            return BusinessFailure("TargetCheckoutUnavailable");
        if (checkout.Checkout!.CheckedOut
            && !IsAllowedTargetCheckout(checkout.Checkout, repo, request.LandingAttemptId))
            return BusinessFailure("TargetCheckedOutElsewhere");

        var targetAlreadyContainsMerge = branchExists
            && await IsAncestorAsync(repo, filters, mergeCommitSha, branchHead.StdOut.Trim(), ct) == true;
        if (checkout.Checkout.CheckedOut)
        {
            var checkedOutBranch = await Git(checkout.Checkout.Path, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            var checkedOutHead = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            var checkedOutStatus = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(checkedOutBranch)
                || !string.Equals(checkedOutBranch.StdOut.Trim(), request.TargetBranch, StringComparison.Ordinal)
                || !Succeeded(checkedOutHead)
                || !string.Equals(checkedOutHead.StdOut.Trim(), branchHead.StdOut.Trim(), StringComparison.OrdinalIgnoreCase)
                || !Succeeded(checkedOutStatus)
                || (!targetAlreadyContainsMerge && !string.IsNullOrWhiteSpace(checkedOutStatus.StdOut)))
                return BusinessFailure("TargetCheckoutChangedBeforeLandingRecovery");
        }
        if (!targetAlreadyContainsMerge)
        {
            if (branchExists
                && !string.Equals(branchHead.StdOut.Trim(), request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                && await IsAncestorAsync(repo, filters, branchHead.StdOut.Trim(), request.TargetHeadSha!, ct) != true)
                return BusinessFailure("TargetMovedBeforeLandingCouldBeCompleted");

            if (checkout.Checkout!.CheckedOut)
            {
                var expectedRefHead = branchExists ? branchHead.StdOut.Trim() : new string('0', mergeCommitSha!.Length);
                var update = await Git(repo, filters, CancellationToken.None,
                    "update-ref", branchRef, mergeCommitSha, expectedRefHead);
                if (!Succeeded(update))
                {
                    var current = await Git(repo, filters, CancellationToken.None, "rev-parse", "--verify", branchRef);
                    if (!Succeeded(current)
                        || await IsAncestorAsync(repo, filters, mergeCommitSha, current.StdOut.Trim(), CancellationToken.None) != true)
                        return BusinessFailure("TargetMovedBeforeLandingCouldBeCompleted");
                }
            }
            else
            {
                var branchStartSha = branchExists ? branchHead.StdOut.Trim() : request.TargetHeadSha!;
                var advanced = await FastForwardTemporaryTargetWorktreeAsync(
                    repo, request.TargetBranch, branchStartSha, mergeCommitSha,
                    request.LandingAttemptId, filters, CancellationToken.None);
                if (!advanced)
                    return BusinessFailure("TargetMovedBeforeLandingCouldBeCompleted");
            }
        }

        var finalHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        if (!Succeeded(finalHead)
            || await IsAncestorAsync(repo, filters, mergeCommitSha, finalHead.StdOut.Trim(), ct) != true)
            return BusinessFailure("LandingCommitNotOnTargetBranch");

        if (checkout.Checkout!.CheckedOut)
        {
            var refreshed = await RefreshTargetCheckoutAsync(
                checkout.Checkout.Path, targetBeforeSha!, finalHead.StdOut.Trim(), filters, CancellationToken.None);
            if (!refreshed)
                return (true, JsonSerializer.Serialize(new
                {
                    status = "landed_unverified",
                    targetBranch = request.TargetBranch,
                    sourceSha = request.SourceSha,
                    targetHeadSha = request.TargetHeadSha,
                    candidateTreeSha = request.TargetTreeSha,
                    mergeCommitSha,
                    postMergeCheckExitCode = (int?)null,
                    postMergeCheckOutputHash = (string?)null,
                    errorCode = "TargetWorktreeUpdateFailed"
                }), null);
            if (SamePath(checkout.Checkout.Path, LandingTargetWorktreePath(request.LandingAttemptId)))
                await RemoveTemporaryWorktreeAsync(repo, checkout.Checkout.Path, filters);
        }

        var postMergeCheck = await RunPostMergeCheckAsync(
            workspace, request, mergeCommitSha, request.TargetTreeSha!, filters, CancellationToken.None);
        if (postMergeCheck.ExitCode != 0 || postMergeCheck.ErrorCode is not null
            || !IsSha256(postMergeCheck.OutputHash))
        {
            return (true, JsonSerializer.Serialize(new
            {
                status = "landed_unverified",
                targetBranch = request.TargetBranch,
                sourceSha = request.SourceSha,
                targetHeadSha = request.TargetHeadSha,
                candidateTreeSha = request.TargetTreeSha,
                mergeCommitSha,
                postMergeCheckExitCode = postMergeCheck.ExitCode,
                postMergeCheckOutputHash = postMergeCheck.OutputHash,
                postMergeCheckOutput = postMergeCheck.Output,
                errorCode = postMergeCheck.ErrorCode ?? "PostMergeCheckFailed"
            }), null);
        }

        return (true, JsonSerializer.Serialize(new
        {
            status = "landed",
            targetBranch = request.TargetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = request.TargetTreeSha,
            mergeCommitSha,
            postMergeCheckExitCode = postMergeCheck.ExitCode,
            postMergeCheckOutputHash = postMergeCheck.OutputHash,
            postMergeCheckOutput = postMergeCheck.Output
        }), null);
    }

    private static async Task<(bool Ok, string? Result, string? Error)?> TryResumeRevertAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var markerRef = LandingMarkerRef(request.LandingAttemptId, "revert");
        var marker = await Git(repo, filters, ct, "rev-parse", "--verify", markerRef);
        if (marker.ExitCode == 128 && !marker.TimedOut)
            return null;
        if (!Succeeded(marker) || !IsGitSha(marker.StdOut.Trim()))
            return BusinessFailure("RevertMarkerInvalid");

        var revertCommitSha = marker.StdOut.Trim();
        var parents = await Git(repo, filters, ct, "rev-list", "--parents", "-n", "1", revertCommitSha);
        var parentFields = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expectedTree = await Git(repo, filters, ct, "rev-parse", "--verify", request.MergeCommitSha + "^1^{tree}");
        var actualTree = await Git(repo, filters, ct, "rev-parse", "--verify", revertCommitSha + "^{tree}");
        if (!Succeeded(parents) || parentFields.Length != 2
            || !string.Equals(parentFields[1], request.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
            || !Succeeded(expectedTree) || !Succeeded(actualTree)
            || !string.Equals(expectedTree.StdOut.Trim(), actualTree.StdOut.Trim(), StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("RevertMarkerDoesNotMatchLanding");

        var branchRef = $"refs/heads/{request.TargetBranch}";
        var branchHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        if (!Succeeded(branchHead))
            return BusinessFailure("TargetBranchUnavailableForRevertRecovery");

        var checkout = await FindTargetCheckoutAsync(repo, request.TargetBranch, filters, ct);
        if (checkout.Error is not null)
            return BusinessFailure("TargetCheckoutUnavailable");

        var targetAlreadyReverted = await IsAncestorAsync(
            repo, filters, revertCommitSha, branchHead.StdOut.Trim(), ct) == true;
        if (checkout.Checkout!.CheckedOut)
        {
            if (!IsAllowedTargetCheckout(checkout.Checkout, repo, request.LandingAttemptId))
                return BusinessFailure("TargetCheckedOutElsewhere");
            var checkedOutBranch = await Git(checkout.Checkout.Path, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            var checkedOutHead = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            var checkedOutStatus = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(checkedOutBranch)
                || !string.Equals(checkedOutBranch.StdOut.Trim(), request.TargetBranch, StringComparison.Ordinal)
                || !Succeeded(checkedOutHead)
                || !string.Equals(checkedOutHead.StdOut.Trim(), branchHead.StdOut.Trim(), StringComparison.OrdinalIgnoreCase)
                || !Succeeded(checkedOutStatus)
                || (!targetAlreadyReverted && !string.IsNullOrWhiteSpace(checkedOutStatus.StdOut)))
                return BusinessFailure("TargetCheckoutChangedBeforeRevertRecovery");
        }
        if (!targetAlreadyReverted)
        {
            if (!string.Equals(branchHead.StdOut.Trim(), request.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("TargetAdvancedBeyondLanding");

            if (checkout.Checkout!.CheckedOut)
            {
                var update = await Git(repo, filters, CancellationToken.None,
                    "update-ref", branchRef, revertCommitSha, request.MergeCommitSha!);
                if (!Succeeded(update))
                {
                    var current = await Git(repo, filters, CancellationToken.None, "rev-parse", "--verify", branchRef);
                    if (!Succeeded(current)
                        || await IsAncestorAsync(repo, filters, revertCommitSha, current.StdOut.Trim(), CancellationToken.None) != true)
                        return BusinessFailure("TargetAdvancedBeyondLanding");
                }
            }
            else
            {
                var reverted = await FastForwardTemporaryTargetWorktreeAsync(
                    repo, request.TargetBranch, request.MergeCommitSha!, revertCommitSha,
                    request.LandingAttemptId, filters, CancellationToken.None);
                if (!reverted)
                    return BusinessFailure("TargetAdvancedBeyondLanding");
            }
        }

        var finalHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        if (!Succeeded(finalHead)
            || await IsAncestorAsync(repo, filters, revertCommitSha, finalHead.StdOut.Trim(), ct) != true)
            return BusinessFailure("RevertCommitNotOnTargetBranch");

        if (checkout.Checkout!.CheckedOut)
        {
            var refreshed = await RefreshTargetCheckoutAsync(
                checkout.Checkout.Path, request.MergeCommitSha!, finalHead.StdOut.Trim(), filters, CancellationToken.None);
            if (!refreshed)
                return (true, JsonSerializer.Serialize(new
                {
                    status = "reverted_unverified",
                    targetBranch = request.TargetBranch,
                    mergeCommitSha = request.MergeCommitSha,
                    revertCommitSha,
                    errorCode = "TargetWorktreeUpdateFailed"
                }), null);
            if (SamePath(checkout.Checkout.Path, LandingTargetWorktreePath(request.LandingAttemptId)))
                await RemoveTemporaryWorktreeAsync(repo, checkout.Checkout.Path, filters);
        }

        return (true, JsonSerializer.Serialize(new
        {
            status = "reverted",
            targetBranch = request.TargetBranch,
            mergeCommitSha = request.MergeCommitSha,
            revertCommitSha
        }), null);
    }

    private static string LandingTargetWorktreePath(Guid attemptId)
    {
        var parent = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "git-land-worktrees");
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $"target-{attemptId:N}");
    }

    /// <summary>
    /// For a branch that is not checked out, performs the target update through a temporary
    /// worktree on that branch using a verified fast-forward. The path is deterministic per
    /// attempt so a process restart can reuse and finish an interrupted operation.
    /// </summary>
    private static async Task<bool> FastForwardTemporaryTargetWorktreeAsync(
        string repositoryRoot,
        string targetBranch,
        string expectedHeadSha,
        string desiredHeadSha,
        Guid attemptId,
        string[] filters,
        CancellationToken ct)
    {
        var targetPath = LandingTargetWorktreePath(attemptId);
        var checkout = await FindTargetCheckoutAsync(repositoryRoot, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return false;

        if (checkout.Checkout!.CheckedOut)
        {
            if (!SamePath(checkout.Checkout.Path, targetPath))
                return false;
        }
        else
        {
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
                return false;

            var branchHead = await Git(repositoryRoot, filters, ct, "rev-parse", "--verify", $"refs/heads/{targetBranch}");
            if (Succeeded(branchHead))
            {
                if (!string.Equals(branchHead.StdOut.Trim(), expectedHeadSha, StringComparison.OrdinalIgnoreCase)
                    && await IsAncestorAsync(repositoryRoot, filters, branchHead.StdOut.Trim(), expectedHeadSha, ct) != true)
                    return false;
                var add = await Git(repositoryRoot, filters, ct, "worktree", "add", targetPath, targetBranch);
                if (!Succeeded(add))
                    return false;
            }
            else
            {
                if (branchHead.ExitCode != 128 || branchHead.TimedOut)
                    return false;
                var add = await Git(repositoryRoot, filters, ct,
                    "worktree", "add", "-b", targetBranch, targetPath, expectedHeadSha);
                if (!Succeeded(add))
                    return false;
            }
        }

        try
        {
            var root = await Git(targetPath, filters, ct, "rev-parse", "--show-toplevel");
            var currentBranch = await Git(targetPath, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            var head = await Git(targetPath, filters, ct, "rev-parse", "--verify", "HEAD");
            var status = await Git(targetPath, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(root) || !SamePath(root.StdOut.Trim(), targetPath)
                || !Succeeded(currentBranch) || !string.Equals(currentBranch.StdOut.Trim(), targetBranch, StringComparison.Ordinal)
                || !Succeeded(head) || !Succeeded(status) || !string.IsNullOrWhiteSpace(status.StdOut))
                return false;

            var currentSha = head.StdOut.Trim();
            if (string.Equals(currentSha, desiredHeadSha, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(currentSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                return false;

            var merge = await Git(targetPath, filters, ct, "merge", "--ff-only", desiredHeadSha);
            if (!Succeeded(merge))
                return false;

            var finalHead = await Git(targetPath, filters, ct, "rev-parse", "--verify", "HEAD");
            var finalStatus = await Git(targetPath, filters, ct, "status", "--porcelain", "--untracked-files=all");
            return Succeeded(finalHead)
                && string.Equals(finalHead.StdOut.Trim(), desiredHeadSha, StringComparison.OrdinalIgnoreCase)
                && Succeeded(finalStatus)
                && string.IsNullOrWhiteSpace(finalStatus.StdOut);
        }
        finally
        {
            // If the process is terminated before this finally executes, the deterministic
            // worktree path remains registered and is reused by the next attempt.
            await RemoveTemporaryWorktreeAsync(repositoryRoot, targetPath, filters);
        }
    }

    private static async Task<bool> RefreshTargetCheckoutAsync(
        string repositoryRoot, string previousHeadSha, string newHeadSha, string[] filters, CancellationToken ct)
    {
        var head = await Git(repositoryRoot, filters, ct, "rev-parse", "--verify", "HEAD");
        if (!Succeeded(head) || !string.Equals(head.StdOut.Trim(), newHeadSha, StringComparison.OrdinalIgnoreCase))
            return false;

        var indexTree = await Git(repositoryRoot, filters, ct, "write-tree");
        var previousTree = await Git(repositoryRoot, filters, ct, "rev-parse", "--verify", previousHeadSha + "^{tree}");
        var nextTree = await Git(repositoryRoot, filters, ct, "rev-parse", "--verify", newHeadSha + "^{tree}");
        var unstaged = await Git(repositoryRoot, filters, ct, "diff", "--quiet");
        var untracked = await Git(repositoryRoot, filters, ct, "ls-files", "--others", "--exclude-standard");
        if (!Succeeded(indexTree) || !Succeeded(previousTree) || !Succeeded(nextTree)
            || unstaged.ExitCode != 0 || unstaged.TimedOut
            || !Succeeded(untracked) || !string.IsNullOrWhiteSpace(untracked.StdOut))
            return false;

        var currentIndexTree = indexTree.StdOut.Trim();
        var expectedPreviousTree = previousTree.StdOut.Trim();
        var expectedNextTree = nextTree.StdOut.Trim();
        if (string.Equals(currentIndexTree, expectedNextTree, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(currentIndexTree, expectedPreviousTree, StringComparison.OrdinalIgnoreCase))
            return false;

        var update = await Git(repositoryRoot, filters, ct, "read-tree", "-u", "-m", previousHeadSha, newHeadSha);
        if (!Succeeded(update))
            return false;
        var updatedHead = await Git(repositoryRoot, filters, ct, "rev-parse", "--verify", "HEAD");
        var updatedTree = await Git(repositoryRoot, filters, ct, "write-tree");
        var status = await Git(repositoryRoot, filters, ct, "status", "--porcelain", "--untracked-files=all");
        return Succeeded(updatedHead)
            && string.Equals(updatedHead.StdOut.Trim(), newHeadSha, StringComparison.OrdinalIgnoreCase)
            && Succeeded(updatedTree)
            && string.Equals(updatedTree.StdOut.Trim(), expectedNextTree, StringComparison.OrdinalIgnoreCase)
            && Succeeded(status)
            && string.IsNullOrWhiteSpace(status.StdOut);
    }

    private static async Task<bool?> IsAncestorAsync(
        string repositoryRoot, string[] filters, string ancestorSha, string descendantSha, CancellationToken ct)
    {
        var result = await Git(repositoryRoot, filters, ct, "merge-base", "--is-ancestor", ancestorSha, descendantSha);
        return result.ExitCode switch
        {
            0 when !result.TimedOut => true,
            1 when !result.TimedOut => false,
            _ => null
        };
    }

    private static string LandingMarkerRef(Guid attemptId, string action) =>
        $"refs/beacon/landing-attempts/{attemptId:N}/{action}";

    private static (bool Ok, string? Result, string? Error) BusinessFailure(string code) =>
        (true, JsonSerializer.Serialize(new { status = "failed", errorCode = code }), null);

    private static async Task<PostMergeCheckResult> RunPostMergeCheckAsync(
        AgentTaskWorktree workspace,
        Request request,
        string mergeCommitSha,
        string expectedTreeSha,
        string[] filters,
        CancellationToken ct)
    {
        var repositoryRoot = workspace.RepositoryRoot;
        var temp = CreateTemporaryWorktreePath(request.LandingAttemptId, "post-merge-check");
        try
        {
            var add = await Git(repositoryRoot, filters, ct, "worktree", "add", "--detach", temp, mergeCommitSha);
            if (!Succeeded(add))
                return new PostMergeCheckResult(null, null, ClipGitOutput(add.StdErr), "PostMergeCheckWorktreeFailed");

            var relativeProject = Path.GetRelativePath(workspace.RecoveryPath, workspace.WorkingDirectory);
            var checkDirectory = relativeProject == "."
                ? temp
                : Path.GetFullPath(Path.Combine(temp, relativeProject));
            if (!IsWithin(temp, checkDirectory) || !Directory.Exists(checkDirectory))
                return new PostMergeCheckResult(null, null, null, "PostMergeCheckProjectMissing");

            var check = EvalCheck.Execute(checkDirectory, request.CheckCommand);
            var output = Clip(check.Output);
            var outputHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(check.Output ?? string.Empty))).ToLowerInvariant();

            var head = await Git(temp, filters, ct, "rev-parse", "--verify", "HEAD");
            var tree = await Git(temp, filters, ct, "write-tree");
            var diff = await Git(temp, filters, ct, "diff", "--quiet");
            var untracked = await Git(temp, filters, ct, "ls-files", "--others", "--exclude-standard");
            var immutable = Succeeded(head)
                && string.Equals(head.StdOut.Trim(), mergeCommitSha, StringComparison.OrdinalIgnoreCase)
                && Succeeded(tree)
                && string.Equals(tree.StdOut.Trim(), expectedTreeSha, StringComparison.OrdinalIgnoreCase)
                && diff.ExitCode == 0 && !diff.TimedOut
                && Succeeded(untracked) && string.IsNullOrWhiteSpace(untracked.StdOut);
            if (!immutable)
                return new PostMergeCheckResult(-2, outputHash, Clip(
                    "The post-merge check modified the merge worktree; its result is not accepted. " + output),
                    "PostMergeCheckModifiedFiles");
            if (check.ExitCode != 0)
                return new PostMergeCheckResult(check.ExitCode, outputHash, output, "PostMergeCheckFailed");
            return new PostMergeCheckResult(0, outputHash, output, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new PostMergeCheckResult(null, null, ClipGitOutput(ex.Message), "PostMergeCheckError");
        }
        finally
        {
            await RemoveTemporaryWorktreeAsync(repositoryRoot, temp, filters);
        }
    }

    private static string ClipGitOutput(string? value)
    {
        var output = value?.Trim() ?? string.Empty;
        return output.Length <= 2000 ? output : output[..2000];
    }

    private static async Task<(bool Ok, string? Result, string? Error)> RevertAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        if (!IsGitSha(request.MergeCommitSha))
            return BusinessFailure("RevertMergeCommitMissing");

        var resumed = await TryResumeRevertAsync(workspace, request, filters, ct);
        if (resumed is { } resumedRevert)
            return resumedRevert;

        var repo = workspace.RepositoryRoot;
        var targetBranch = request.TargetBranch;
        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return BusinessFailure("TargetCheckoutUnavailable");
        if (checkout.Checkout!.CheckedOut
            && !IsAllowedTargetCheckout(checkout.Checkout, repo, request.LandingAttemptId))
            return BusinessFailure("TargetCheckedOutElsewhere");

        var branchRef = $"refs/heads/{targetBranch}";
        var head = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        if (!Succeeded(head) || !string.Equals(head.StdOut.Trim(), request.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            return BusinessFailure("TargetAdvancedBeyondLanding");

        if (checkout.Checkout.CheckedOut)
        {
            var clean = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            var currentBranch = await Git(checkout.Checkout.Path, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            var currentHead = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(clean) || !string.IsNullOrWhiteSpace(clean.StdOut)
                || !Succeeded(currentBranch)
                || !string.Equals(currentBranch.StdOut.Trim(), targetBranch, StringComparison.Ordinal)
                || !Succeeded(currentHead)
                || !string.Equals(currentHead.StdOut.Trim(), request.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("TargetCheckoutChangedBeforeRevert");
        }

        var temp = CreateTemporaryWorktreePath(request.LandingAttemptId, "revert");
        try
        {
            var add = await Git(repo, filters, ct, "worktree", "add", "--detach", temp, request.MergeCommitSha!);
            if (!Succeeded(add))
                return BusinessFailure("RevertWorktreeFailed");

            var revert = await Git(temp, filters, ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true",
                "revert", "--no-edit", "-m", "1", request.MergeCommitSha!);
            if (!Succeeded(revert))
            {
                var abort = await Git(temp, filters, ct, "revert", "--abort");
                var cleanup = Succeeded(abort)
                    ? string.Empty
                    : " The detached revert could not be aborted automatically; inspect its temporary worktree before continuing.";
                return BusinessFailure("GitRevertFailed" + cleanup);
            }

            var resultHead = await Git(temp, filters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(resultHead) || !IsGitSha(resultHead.StdOut.Trim()))
                return BusinessFailure("RevertCommitUnavailable");
            var revertCommitSha = resultHead.StdOut.Trim();

            var parents = await Git(temp, filters, ct, "rev-list", "--parents", "-n", "1", revertCommitSha);
            var parentFields = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!Succeeded(parents) || parentFields.Length != 2
                || !string.Equals(parentFields[1], request.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("RevertCommitParentInvalid");

            var expectedTree = await Git(repo, filters, ct, "rev-parse", "--verify", request.MergeCommitSha + "^1^{tree}");
            var revertTree = await Git(temp, filters, ct, "rev-parse", "--verify", revertCommitSha + "^{tree}");
            if (!Succeeded(expectedTree) || !Succeeded(revertTree)
                || !string.Equals(expectedTree.StdOut.Trim(), revertTree.StdOut.Trim(), StringComparison.OrdinalIgnoreCase))
                return BusinessFailure("RevertCommitTreeInvalid");

            // The revert marker makes an interrupted CAS recoverable without creating a
            // second revert commit or accidentally reverting a later target change.
            var markerRef = LandingMarkerRef(request.LandingAttemptId, "revert");
            var markerCreate = await Git(repo, filters, CancellationToken.None,
                "update-ref", markerRef, revertCommitSha, new string('0', revertCommitSha.Length));
            if (!Succeeded(markerCreate))
            {
                var recovered = await TryResumeRevertAsync(workspace, request, filters, CancellationToken.None);
                return recovered ?? BusinessFailure("RevertMarkerCouldNotBePersisted");
            }

            var update = await Git(repo, filters, CancellationToken.None,
                "update-ref", branchRef, revertCommitSha, request.MergeCommitSha!);
            if (!Succeeded(update))
            {
                var recovered = await TryResumeRevertAsync(workspace, request, filters, CancellationToken.None);
                return recovered ?? BusinessFailure("TargetChangedBeforeRevertUpdate");
            }

            return await TryResumeRevertAsync(workspace, request, filters, CancellationToken.None)
                ?? BusinessFailure("RevertMarkerCouldNotBeReconciled");
        }
        finally
        {
            await RemoveTemporaryWorktreeAsync(repo, temp, filters);
        }
    }

    private static async Task<CandidateResult> BuildCandidateAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var targetBranch = request.TargetBranch;
        var branchCheck = await Git(repo, filters, ct, "check-ref-format", "--branch", targetBranch);
        if (!Succeeded(branchCheck))
            return new CandidateResult("failed", ErrorCode: "InvalidTargetBranch");

        var target = await ResolveTargetHeadAsync(
            workspace, request.TargetBranch, request.ReviewBaseBranch, request.LandingAttemptId, filters, ct);
        if (target.Error is not null)
        {
            if (target.ErrorCode == "TargetCheckoutDirty"
                && request.AllowIntegrationFallback
                && !string.Equals(targetBranch, IntegrationBranchName, StringComparison.Ordinal))
            {
                return await BuildCandidateAsync(
                    workspace,
                    request with { TargetBranch = IntegrationBranchName, AllowIntegrationFallback = false },
                    filters,
                    ct);
            }
            return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: target.ErrorCode ?? "TargetUnavailable");
        }

        // --write-tree computes the merge result directly in Git's object database. It does
        // not create MERGE_HEAD, touch an index, or modify the target checkout. With -z and
        // --name-only the output is: <tree-sha> NUL [conflict-path NUL ...].
        var mergeTree = await Git(
            repo, filters, ct,
            "merge-tree", "--write-tree", "--name-only", "-z", "--no-messages",
            target.Sha!, request.SourceSha);
        if (mergeTree.TimedOut || mergeTree.ExitCode is not (0 or 1))
            return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "MergeTreeFailed");

        var mergeFields = mergeTree.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (mergeFields.Length == 0 || !IsGitSha(mergeFields[0]))
            return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "MergeTreeResultInvalid");
        var treeSha = mergeFields[0];

        var projectPrefix = Path.GetRelativePath(workspace.RecoveryPath, workspace.WorkingDirectory)
            .Replace('\\', '/')
            .Trim('/');
        if (mergeTree.ExitCode == 1)
        {
            var conflicts = mergeFields.Skip(1).ToList();
            if (conflicts.Count == 0
                || conflicts.Any(path => string.IsNullOrWhiteSpace(path)
                    || path.StartsWith("/", StringComparison.Ordinal)
                    || path.Split('/').Any(segment => segment is "" or "." or "..")))
                return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "MergeTreeConflictListInvalid");

            if (projectPrefix.Length > 0 && projectPrefix != ".")
            {
                var prefix = projectPrefix + "/";
                if (conflicts.Any(path => !path.StartsWith(prefix, StringComparison.Ordinal)))
                    return new CandidateResult(
                        "failed", targetBranch, target.Sha, ErrorCode: "ConflictsOutsideProject");
                conflicts = conflicts.Select(path => path[prefix.Length..]).ToList();
            }

            return new CandidateResult("conflicted", targetBranch, target.Sha, Conflicts: conflicts);
        }

        if (mergeFields.Length != 1)
            return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "UnexpectedMergeTreeOutput");

        // Materialize the immutable candidate tree as an unreferenced commit so Git can
        // check it out in a temporary worktree for the configured verification command.
        // The target ref and every registered checkout remain untouched during Preview.
        var candidateCommit = await Git(repo, filters, ct,
            "-c", "user.name=ProjectBeacon",
            "-c", "user.email=projectbeacon@localhost",
            "-c", "commit.gpgsign=false",
            "commit-tree", treeSha,
            "-p", target.Sha!,
            "-p", request.SourceSha,
            "-m", $"ProjectBeacon: preview task {request.TaskId:N}");
        if (!Succeeded(candidateCommit) || !IsGitSha(candidateCommit.StdOut.Trim()))
            return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateCommitFailed");

        var temp = CreateTemporaryWorktreePath(request.LandingAttemptId, "candidate");
        try
        {
            var add = await Git(repo, filters, ct, "worktree", "add", "--detach", temp, candidateCommit.StdOut.Trim());
            if (!Succeeded(add))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateWorktreeFailed");

            var treeEntries = await Git(temp, filters, ct, "ls-tree", "-r", treeSha);
            if (!Succeeded(treeEntries))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateTreeInspectionFailed");
            if (treeEntries.StdOut.Split('\n').Any(line =>
                line.StartsWith("120000 blob ", StringComparison.Ordinal)
                || line.StartsWith("160000 commit ", StringComparison.Ordinal)))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "SymlinkOrSubmoduleNotAllowed");

            var scope = await CheckCandidateScopeAsync(
                temp, target.Sha!, projectPrefix, request.MaxChangedFiles, request.MaxChangedLines,
                request.ProtectedPaths, filters, ct);
            if (scope.Error is not null)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: scope.Error);
            if (scope.ProtectedPath)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "ProtectedPathChanged");
            if (scope.FileCount > request.MaxChangedFiles || scope.ChangedLines > request.MaxChangedLines)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateDiffTooLarge");

            var relativeProject = Path.GetRelativePath(workspace.RecoveryPath, workspace.WorkingDirectory);
            var checkDirectory = relativeProject == "."
                ? temp
                : Path.GetFullPath(Path.Combine(temp, relativeProject));
            if (!IsWithin(temp, checkDirectory) || !Directory.Exists(checkDirectory))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateProjectMissing");

            var check = EvalCheck.Execute(checkDirectory, request.CheckCommand);
            var outputHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(check.Output ?? string.Empty))).ToLowerInvariant();
            var headAfter = await Git(temp, filters, ct, "rev-parse", "--verify", "HEAD");
            var workingDiff = await Git(temp, filters, ct, "diff", "--quiet");
            var untracked = await Git(temp, filters, ct, "ls-files", "--others", "--exclude-standard");
            var afterTree = await Git(temp, filters, ct, "write-tree");
            if (!Succeeded(headAfter)
                || !string.Equals(headAfter.StdOut.Trim(), candidateCommit.StdOut.Trim(), StringComparison.OrdinalIgnoreCase)
                || workingDiff.ExitCode != 0 || workingDiff.TimedOut
                || !Succeeded(untracked) || !string.IsNullOrWhiteSpace(untracked.StdOut)
                || !Succeeded(afterTree) || !string.Equals(afterTree.StdOut.Trim(), treeSha, StringComparison.OrdinalIgnoreCase))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, outputHash, check.ExitCode,
                    Clip(check.Output), ErrorCode: "CandidateCheckModifiedFiles");
            if (check.ExitCode != 0)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, outputHash, check.ExitCode,
                    Clip(check.Output), ErrorCode: "CandidateCheckFailed");

            return new CandidateResult("previewed", targetBranch, target.Sha, treeSha, outputHash, check.ExitCode, Clip(check.Output));
        }
        finally
        {
            await RemoveTemporaryWorktreeAsync(repo, temp, filters);
        }
    }

    private static async Task<(string? Sha, string? Error, string? ErrorCode)> ResolveTargetHeadAsync(
        AgentTaskWorktree workspace, string targetBranch, string reviewBaseBranch, Guid attemptId, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return (null, checkout.Error, "TargetCheckoutUnavailable");
        if (checkout.Checkout!.CheckedOut)
        {
            if (!SamePath(checkout.Checkout.Path, repo))
                return (null, "The target branch is checked out in a different worktree.", "TargetCheckedOutElsewhere");
            var status = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(status) || !string.IsNullOrWhiteSpace(status.StdOut))
                return (null, "The target checkout is not clean.", "TargetCheckoutDirty");
            var head = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            return Succeeded(head) && IsGitSha(head.StdOut.Trim())
                ? (head.StdOut.Trim(), null, null)
                : (null, "Could not read target checkout HEAD.", "TargetHeadUnavailable");
        }

        var remotes = await Git(repo, filters, ct, "remote");
        if (!Succeeded(remotes))
            return (null, "Could not inspect configured Git remotes safely.", "TargetRemoteInspectionFailed");
        var hasOrigin = remotes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name.Trim(), "origin", StringComparison.Ordinal));
        if (hasOrigin)
        {
            var remote = await Git(repo, filters, ct, "remote", "get-url", "origin");
            if (!Succeeded(remote) || string.IsNullOrWhiteSpace(remote.StdOut))
                return (null, "Could not resolve the configured origin URL safely.", "TargetRemoteInspectionFailed");
            var url = remote.StdOut.Trim();
            if (!AgentTaskWorktree.IsSupportedFetchRemote(url))
                return (null, "The configured origin uses an unsupported Git transport.", "UnsupportedRemote");
            var fetchBranch = targetBranch;
            if (string.Equals(targetBranch, IntegrationBranchName, StringComparison.Ordinal))
            {
                var remoteBranch = await Git(repo, filters, ct,
                    "ls-remote", "--heads", "--", url, $"refs/heads/{IntegrationBranchName}");
                if (!Succeeded(remoteBranch))
                    return (null, "Could not inspect the configured remote integration branch.", "TargetRemoteInspectionFailed");
                var integrationExistsRemotely = remoteBranch.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.Split('\t').LastOrDefault() == $"refs/heads/{IntegrationBranchName}");
                if (!integrationExistsRemotely)
                {
                    var localIntegration = await Git(
                        repo, filters, ct, "rev-parse", "--verify", $"refs/heads/{IntegrationBranchName}");
                    if (Succeeded(localIntegration) && IsGitSha(localIntegration.StdOut.Trim()))
                        return (localIntegration.StdOut.Trim(), null, null);
                    if (localIntegration.TimedOut || localIntegration.ExitCode != 128)
                        return (null, "Could not inspect the local integration branch safely.", "TargetHeadUnavailable");
                    fetchBranch = reviewBaseBranch;
                }
            }

            var targetRef = $"refs/beacon/landing/{attemptId:N}/target";
            var fetch = await Git(repo, filters, ct,
                "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
                "--", url, $"+refs/heads/{fetchBranch}:{targetRef}");
            if (!Succeeded(fetch))
                return (null, "Could not fetch the daemon-selected target branch or reviewed base.", "TargetFetchFailed");
            var fetched = await Git(repo, filters, ct, "rev-parse", "--verify", targetRef);
            return Succeeded(fetched) && IsGitSha(fetched.StdOut.Trim())
                ? (fetched.StdOut.Trim(), null, null)
                : (null, "Could not verify the fetched target branch.", "TargetHeadUnavailable");
        }

        // Local-only repositories are supported, but a configured origin must never
        // silently fall back to a stale local copy when its fetch fails.
        var local = await Git(repo, filters, ct, "rev-parse", "--verify", $"refs/heads/{targetBranch}");
        if (Succeeded(local) && IsGitSha(local.StdOut.Trim()))
            return (local.StdOut.Trim(), null, null);
        if (local.TimedOut || local.ExitCode != 128)
            return (null, "Could not inspect the local target branch safely.", "TargetHeadUnavailable");
        if (string.Equals(targetBranch, IntegrationBranchName, StringComparison.Ordinal))
        {
            var localBase = await Git(repo, filters, ct, "rev-parse", "--verify", $"refs/heads/{reviewBaseBranch}");
            if (Succeeded(localBase) && IsGitSha(localBase.StdOut.Trim()))
                return (localBase.StdOut.Trim(), null, null);
            if (localBase.TimedOut || localBase.ExitCode != 128)
                return (null, "Could not inspect the reviewed base branch safely.", "TargetHeadUnavailable");
        }
        return (null, "The target and reviewed base branches are unavailable in this local-only repository.", "TargetUnavailable");
    }

    private static async Task<(string? Error, int FileCount, int ChangedLines, bool ProtectedPath)> CheckCandidateScopeAsync(
        string candidateWorktree,
        string targetHeadSha,
        string projectPrefix,
        int maxChangedFiles,
        int maxChangedLines,
        IReadOnlyList<string> additionalProtectedPaths,
        string[] filters,
        CancellationToken ct)
    {
        // A rename is represented by both its deleted source and added destination.
        // Without --no-renames Git can report only the destination, hiding a protected or
        // out-of-project source path from the landing policy.
        var files = await Git(candidateWorktree, filters, ct,
            "diff", "--cached", "--no-renames", "--name-only", "-z", targetHeadSha);
        if (!Succeeded(files))
            return ("CandidateDiffInspectionFailed", 0, 0, false);
        var paths = files.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var prefix = string.IsNullOrWhiteSpace(projectPrefix) || projectPrefix == "."
            ? string.Empty
            : projectPrefix.Trim('/') + "/";
        if (prefix.Length > 0
            && paths.Any(path => !path.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)))
            return ("CandidateChangesOutsideProject", paths.Length, 0, false);
        var projectPaths = prefix.Length == 0
            ? paths
            : paths.Select(path => path.Replace('\\', '/')[prefix.Length..]).ToArray();
        bool Protected(string path)
        {
            var value = path.Replace('\\', '/');
            var builtIn = value.Equals("SECURITY.md", StringComparison.OrdinalIgnoreCase)
                || value.Equals(".beacon/release.json", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                || value.Equals(".github/workflows", StringComparison.OrdinalIgnoreCase)
                || value.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
            return builtIn || additionalProtectedPaths.Any(pattern => MatchesPathPattern(pattern, value));
        }
        var protectedPath = projectPaths.Any(Protected);
        var numstat = await Git(candidateWorktree, filters, ct,
            "diff", "--cached", "--no-renames", "--numstat", targetHeadSha);
        if (!Succeeded(numstat))
            return ("CandidateDiffInspectionFailed", paths.Length, 0, protectedPath);
        var changedLines = 0;
        foreach (var line in numstat.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            if (int.TryParse(parts[0], out var added))
                changedLines += added;
            if (int.TryParse(parts[1], out var removed))
                changedLines += removed;
        }
        if (paths.Length > maxChangedFiles || changedLines > maxChangedLines)
            return ("CandidateDiffTooLarge", paths.Length, changedLines, protectedPath);
        return (null, paths.Length, changedLines, protectedPath);
    }

    private static bool MatchesPathPattern(string pattern, string path)
    {
        var expression = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<List<string>> ReadConflictsAsync(string worktree, string[] filters, CancellationToken ct)
    {
        var result = await Git(worktree, filters, ct, "ls-files", "-u", "-z");
        if (!Succeeded(result))
            return [];
        return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry =>
            {
                var tab = entry.IndexOf('\t');
                return tab >= 0 ? entry[(tab + 1)..] : string.Empty;
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<(TargetCheckout? Checkout, string? Error)> FindTargetCheckoutAsync(
        string repositoryRoot, string branch, string[] filters, CancellationToken ct)
    {
        var result = await Git(repositoryRoot, filters, ct, "worktree", "list", "--porcelain");
        if (!Succeeded(result))
            return (null, "Could not inspect registered Git worktrees.");
        string? path = null;
        var branchRef = $"branch refs/heads/{branch}";
        foreach (var line in result.StdOut.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                path = line["worktree ".Length..].Trim();
            else if (line.Equals(branchRef, StringComparison.Ordinal) && path is not null)
                return (new TargetCheckout(Path.GetFullPath(path), true), null);
        }
        return (new TargetCheckout(repositoryRoot, false), null);
    }

    private static async Task<(string[]? Filters, string? Error)> ReadDisabledFiltersAsync(string repositoryRoot, CancellationToken ct)
    {
        var config = await Git(repositoryRoot, [], ct, "config", "--name-only", "--get-regexp", "^filter\\.");
        if (config.TimedOut || config.ExitCode is not (0 or 1))
            return (null, "Could not inspect Git filter configuration safely.");
        return (AgentSessionWorktree.ParseFilterDriverNames(config.StdOut), null);
    }

    private static string CreateTemporaryWorktreePath(Guid attemptId, string purpose)
    {
        var parent = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "git-land-worktrees");
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $"{purpose}-{attemptId:N}-{Guid.NewGuid():N}");
    }

    private static async Task RemoveTemporaryWorktreeAsync(string repositoryRoot, string path, string[] filters)
    {
        _ = await Git(repositoryRoot, filters, CancellationToken.None, "worktree", "remove", "--force", path);
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
        _ = await Git(repositoryRoot, filters, CancellationToken.None, "worktree", "prune");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> Git(
        string directory, IReadOnlyCollection<string> filters, CancellationToken ct, params string[] args) =>
        AgentSessionWorktree.RunGitAsync(directory, ct, filters, args);

    private static bool Succeeded((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
        result.ExitCode == 0 && !result.TimedOut;

    private static bool IsSafeBranchName(string? value) =>
        value is { Length: > 0 and <= 200 }
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && value.All(character => !char.IsControl(character)
            && character is not (' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'));

    private static bool IsAllowedTargetCheckout(TargetCheckout checkout, string repositoryRoot, Guid attemptId) =>
        !checkout.CheckedOut
        || SamePath(checkout.Path, repositoryRoot)
        || SamePath(checkout.Path, LandingTargetWorktreePath(attemptId));

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryReadRequest(string json, out Request? request, out string? error)
    {
        request = null;
        error = null;
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null
                || !Guid.TryParse(root["projectId"]?.GetValue<string>(), out var projectId) || projectId == Guid.Empty
                || !Guid.TryParse(root["taskId"]?.GetValue<string>(), out var taskId) || taskId == Guid.Empty
                || !Guid.TryParse(root["landingAttemptId"]?.GetValue<string>(), out var attemptId) || attemptId == Guid.Empty)
            {
                error = "GitLand payload identity is invalid.";
                return false;
            }
            var action = root["action"]?.GetValue<string>();
            var targetBranch = root["targetBranch"]?.GetValue<string>();
            var reviewBaseBranch = root["reviewBaseBranch"]?.GetValue<string>() ?? targetBranch;
            var allowIntegrationFallback = root["allowIntegrationFallback"]?.GetValue<bool>() ?? false;
            var sourceSha = root["sourceSha"]?.GetValue<string>();
            var reviewTreeSha = root["reviewTreeSha"]?.GetValue<string>();
            var hash = root["checkCommandHash"]?.GetValue<string>();
            var checkCommand = root["checkCommand"]?.GetValue<string>();
            var maxFiles = root["maxChangedFiles"]?.GetValue<int>() ?? 0;
            var maxLines = root["maxChangedLines"]?.GetValue<int>() ?? 0;
            var protectedPaths = root["protectedPaths"] is JsonArray protectedArray
                ? protectedArray.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
                : [];
            if (maxFiles is < 1 or > MaxChangedFiles
                || maxLines is < 1 or > MaxChangedLines
                || protectedPaths.Length > 100
                || protectedPaths.Any(path => !IsSafeProtectedPattern(path)))
            {
                error = "GitLand policy limits or protected path patterns are invalid.";
                return false;
            }
            if (action is not ("Preview" or "Execute" or "Revert")
                || !IsSafeBranchName(targetBranch)
                || !IsSafeBranchName(reviewBaseBranch)
                || (!string.Equals(targetBranch, reviewBaseBranch, StringComparison.Ordinal)
                    && !string.Equals(targetBranch, IntegrationBranchName, StringComparison.Ordinal))
                || (allowIntegrationFallback
                    && (action != "Preview"
                        || !string.Equals(targetBranch, reviewBaseBranch, StringComparison.Ordinal)))
                || !IsGitSha(sourceSha)
                || !IsGitSha(reviewTreeSha)
                || hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)
                || (action is not "Revert" && string.IsNullOrWhiteSpace(checkCommand))
                || checkCommand?.Length > 2000)
            {
                error = "GitLand payload action, source SHA, or trusted check command is invalid.";
                return false;
            }
            request = new Request(
                projectId, taskId, attemptId, action, targetBranch!, reviewBaseBranch!, allowIntegrationFallback,
                sourceSha!, reviewTreeSha!, hash!, checkCommand ?? string.Empty,
                root["targetHeadSha"]?.GetValue<string>(),
                root["targetTreeSha"]?.GetValue<string>(),
                root["mergeCommitSha"]?.GetValue<string>(),
                maxFiles,
                maxLines,
                protectedPaths);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "GitLand payload is malformed.";
            return false;
        }
    }

    private static bool IsSafeProtectedPattern(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern)
        && pattern.Length <= 200
        && Regex.IsMatch(pattern, "^[A-Za-z0-9._/*?-]+$", RegexOptions.CultureInvariant)
        && !pattern.StartsWith("/", StringComparison.Ordinal)
        && !pattern.Contains("..", StringComparison.Ordinal)
        && !pattern.Contains('\\')
        && !pattern.Contains(':')
        && !pattern.Split('/').Any(segment => segment.Length == 0);

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string HashCommand(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Clip(string? value)
    {
        var output = value?.Trim() ?? string.Empty;
        return output.Length <= 12_000 ? output : output[..12_000] + "\n[output truncated]";
    }
}
