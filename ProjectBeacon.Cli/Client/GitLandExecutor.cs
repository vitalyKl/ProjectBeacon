namespace ProjectBeacon.Cli.Client;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Executes daemon-authorized Git landing operations. All repository, task branch, target branch,
/// and remote identity are derived from the persistent task worktree metadata.
/// </summary>
internal static class GitLandExecutor
{
    private const int MaxChangedFiles = 200;
    private const int MaxChangedLines = 20_000;

    private sealed record Request(
        Guid ProjectId,
        Guid TaskId,
        Guid LandingAttemptId,
        string Action,
        string SourceSha,
        string CheckCommandHash,
        string CheckCommand,
        string? TargetHeadSha,
        string? TargetTreeSha,
        string? MergeCommitSha);

    private sealed record TargetCheckout(string Path, bool CheckedOut);
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
            return (false, null, "GitLand requires persistent task branch workspace mode.");
        if (authorizedProjectId == Guid.Empty || !Directory.Exists(projectRoot))
            return (false, null, "GitLand requires a valid project runtime.");

        if (!TryReadRequest(payloadJson, out var request, out var parseError))
            return (false, null, parseError);
        if (request!.ProjectId != authorizedProjectId)
            return (false, null, "GitLand project identity does not match the workstation command.");

        if (!string.Equals(HashCommand(request.CheckCommand), request.CheckCommandHash, StringComparison.OrdinalIgnoreCase))
            return (false, null, "The trusted review command does not match its server-pinned hash.");

        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, request.ProjectId, request.TaskId, settings.WorktreeRoot, ct);
        if (opened.Worktree is null)
            return (false, null, opened.Error ?? "The persistent task branch workspace is unavailable.");

        await using var taskWorkspace = opened.Worktree;
        var expectedBranch = $"beacon/task/{request.TaskId:N}";
        if (!string.Equals(taskWorkspace.BranchName, expectedBranch, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(taskWorkspace.BaseBranch))
            return (false, null, "The task branch workspace has no daemon-selected target branch.");

        var repositoryRoot = taskWorkspace.RepositoryRoot;
        var filters = await ReadDisabledFiltersAsync(repositoryRoot, ct);
        if (filters.Error is not null)
            return (false, null, filters.Error);
        var disabledFilters = filters.Filters!;

        var version = await Git(repositoryRoot, disabledFilters, ct, "--version");
        if (!Succeeded(version) || !AgentTaskWorktree.SupportsBranchWorkflow(version.StdOut))
            return (false, null, "GitLand requires Git 2.38 or newer.");

        var branch = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!Succeeded(branch) || !string.Equals(branch.StdOut.Trim(), expectedBranch, StringComparison.Ordinal))
            return (false, null, "The task worktree is no longer on its assigned task branch.");

        if (request.Action is not "Revert")
        {
            var sourceHead = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(sourceHead) || !string.Equals(sourceHead.StdOut.Trim(), request.SourceSha, StringComparison.OrdinalIgnoreCase))
                return (false, null, "The task branch has changed since the successful review check.");

            var sourceStatus = await Git(taskWorkspace.RecoveryPath, disabledFilters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(sourceStatus))
                return (false, null, "Could not verify task branch cleanliness.");
            if (!string.IsNullOrWhiteSpace(sourceStatus.StdOut))
                return (false, null, "The task branch contains uncommitted changes; commit them and repeat review before landing.");
        }

        var customMergeDrivers = await Git(repositoryRoot, disabledFilters, ct, "config", "--get-regexp", "^merge\\..*\\.driver$");
        if (customMergeDrivers.TimedOut || customMergeDrivers.ExitCode is not (0 or 1))
            return (false, null, "Could not inspect merge driver configuration safely.");
        if (customMergeDrivers.ExitCode == 0 && !string.IsNullOrWhiteSpace(customMergeDrivers.StdOut))
            return (false, null, "Custom Git merge drivers are configured; GitLand refuses to execute repository-defined merge programs.");

        return request.Action switch
        {
            "Preview" => await PreviewAsync(taskWorkspace, request, disabledFilters, ct),
            "Execute" => await ExecuteLandingAsync(taskWorkspace, request, disabledFilters, ct),
            "Revert" => await RevertAsync(taskWorkspace, request, disabledFilters, ct),
            _ => (false, null, "GitLand action must be Preview, Execute, or Revert.")
        };
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
                targetBranch = workspace.BaseBranch,
                targetHeadSha = candidate.TargetHeadSha,
                candidateTreeSha = candidate.CandidateTreeSha,
                checkOutputHash = candidate.CheckOutputHash,
                checkExitCode = candidate.CheckExitCode,
                checkOutput = candidate.CheckOutput
            }), null),
            "conflicted" => (true, JsonSerializer.Serialize(new
            {
                status = "conflicted",
                targetBranch = workspace.BaseBranch,
                targetHeadSha = candidate.TargetHeadSha,
                conflicts = candidate.Conflicts
            }), null),
            _ => (true, JsonSerializer.Serialize(new
            {
                status = "failed",
                targetBranch = workspace.BaseBranch,
                targetHeadSha = candidate.TargetHeadSha,
                errorCode = candidate.ErrorCode ?? candidate.Status,
                checkExitCode = candidate.CheckExitCode,
                checkOutput = candidate.CheckOutput
            }), null)
        };
    }

    private static async Task<(bool Ok, string? Result, string? Error)> ExecuteLandingAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        if (!IsGitSha(request.TargetHeadSha) || !IsGitSha(request.TargetTreeSha))
            return (false, null, "GitLand Execute requires the reviewed target HEAD and candidate tree.");

        var candidate = await BuildCandidateAsync(workspace, request, filters, ct);
        if (candidate.Status != "previewed")
            return (false, JsonSerializer.Serialize(new { status = candidate.Status, conflicts = candidate.Conflicts, errorCode = candidate.ErrorCode }), "The merge candidate no longer passes preview.");
        if (!string.Equals(candidate.TargetHeadSha, request.TargetHeadSha, StringComparison.OrdinalIgnoreCase))
            return (false, null, "Target branch HEAD changed after preview; a new landing preview is required.");
        if (!string.Equals(candidate.CandidateTreeSha, request.TargetTreeSha, StringComparison.OrdinalIgnoreCase))
            return (false, null, "The candidate tree changed after preview; a new landing preview is required.");

        var repo = workspace.RepositoryRoot;
        var targetBranch = workspace.BaseBranch!;
        var branchCheck = await Git(repo, filters, ct, "check-ref-format", "--branch", targetBranch);
        if (!Succeeded(branchCheck))
            return (false, null, "The daemon-selected target branch is not a valid branch name.");

        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return (false, null, checkout.Error);

        string mergeCommitSha;
        if (checkout.Checkout!.CheckedOut)
        {
            if (!SamePath(checkout.Checkout.Path, repo))
                return (false, null, "The target branch is checked out in a different worktree; GitLand will not mutate an unrelated checkout.");
            var clean = await Git(repo, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(clean) || !string.IsNullOrWhiteSpace(clean.StdOut))
                return (false, null, "The target checkout is not clean; landing was not executed.");
            var currentBranch = await Git(repo, filters, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            if (!Succeeded(currentBranch) || !string.Equals(currentBranch.StdOut.Trim(), targetBranch, StringComparison.Ordinal))
                return (false, null, "The target checkout changed branch; landing was not executed.");

            var merge = await Git(repo, filters, ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true",
                "merge", "--no-ff", "--no-edit", "--no-verify", request.SourceSha);
            if (!Succeeded(merge))
                return (false, null, "Git could not apply the reviewed merge in the target checkout. No reset or destructive recovery was attempted.");
            var head = await Git(repo, filters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(head) || !IsGitSha(head.StdOut.Trim()))
                return (false, null, "The merge ran but its resulting commit SHA could not be verified.");
            mergeCommitSha = head.StdOut.Trim();
            var parents = await Git(repo, filters, ct, "rev-list", "--parents", "-n", "1", mergeCommitSha);
            var parentFields = parents.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!Succeeded(parents) || parentFields.Length != 3
                || !string.Equals(parentFields[1], request.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parentFields[2], request.SourceSha, StringComparison.OrdinalIgnoreCase))
                return (false, JsonSerializer.Serialize(new { status = "landed_metadata_mismatch", mergeCommitSha }),
                    "The target moved while the merge was being created. The merge commit was not reset; inspect the target branch before retrying.");
        }
        else
        {
            var branchRef = $"refs/heads/{targetBranch}";
            var localHead = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
            if (!Succeeded(localHead) || !string.Equals(localHead.StdOut.Trim(), request.TargetHeadSha, StringComparison.OrdinalIgnoreCase))
                return (false, null, "The local target branch moved after preview; landing was not executed.");

            var commit = await Git(repo, filters, ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon@localhost",
                "-c", "commit.gpgsign=false",
                "commit-tree", candidate.CandidateTreeSha!,
                "-p", request.TargetHeadSha!,
                "-p", request.SourceSha,
                "-m", $"ProjectBeacon: land task {request.TaskId:N}");
            if (!Succeeded(commit) || !IsGitSha(commit.StdOut.Trim()))
                return (false, null, "Could not create the verified landing merge commit.");
            mergeCommitSha = commit.StdOut.Trim();

            var update = await Git(repo, filters, ct,
                "update-ref", branchRef, mergeCommitSha, request.TargetHeadSha!);
            if (!Succeeded(update))
                return (false, null, "The target branch changed before the atomic landing update; the branch was not moved.");
        }

        var mergeTree = await Git(repo, filters, ct, "rev-parse", "--verify", mergeCommitSha + "^{tree}");
        if (!Succeeded(mergeTree) || !string.Equals(mergeTree.StdOut.Trim(), candidate.CandidateTreeSha, StringComparison.OrdinalIgnoreCase))
            return (false, JsonSerializer.Serialize(new { status = "landed_tree_mismatch", mergeCommitSha, actualTreeSha = mergeTree.StdOut.Trim(), expectedTreeSha = candidate.CandidateTreeSha }),
                "The merge commit exists but its tree differs from the validated candidate. Do not retry automatically; inspect the target branch.");
        return (true, JsonSerializer.Serialize(new
        {
            status = "landed",
            targetBranch,
            sourceSha = request.SourceSha,
            targetHeadSha = request.TargetHeadSha,
            candidateTreeSha = candidate.CandidateTreeSha,
            mergeCommitSha
        }), null);
    }

    private static async Task<(bool Ok, string? Result, string? Error)> RevertAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        if (!IsGitSha(request.MergeCommitSha))
            return (false, null, "GitLand Revert requires the recorded merge commit SHA.");
        var repo = workspace.RepositoryRoot;
        var targetBranch = workspace.BaseBranch!;
        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return (false, null, checkout.Error);
        var branchRef = $"refs/heads/{targetBranch}";
        var head = await Git(repo, filters, ct, "rev-parse", "--verify", branchRef);
        if (!Succeeded(head) || !string.Equals(head.StdOut.Trim(), request.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            return (false, null, "The target branch has advanced beyond the landing commit; automatic revert is not safe.");

        string revertCommitSha;
        if (checkout.Checkout!.CheckedOut)
        {
            if (!SamePath(checkout.Checkout.Path, repo))
                return (false, null, "The target branch is checked out in a different worktree; revert was not attempted.");
            var clean = await Git(repo, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(clean) || !string.IsNullOrWhiteSpace(clean.StdOut))
                return (false, null, "The target checkout is not clean; revert was not attempted.");
            var revert = await Git(repo, filters, ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true",
                "revert", "--no-edit", "-m", "1", request.MergeCommitSha!);
            if (!Succeeded(revert))
                return (false, null, "Git could not revert the landing commit. No reset or destructive recovery was attempted.");
            var resultHead = await Git(repo, filters, ct, "rev-parse", "--verify", "HEAD");
            if (!Succeeded(resultHead) || !IsGitSha(resultHead.StdOut.Trim()))
                return (false, null, "The revert ran but its commit SHA could not be verified.");
            revertCommitSha = resultHead.StdOut.Trim();
        }
        else
        {
            var temp = CreateTemporaryWorktreePath(request.LandingAttemptId, "revert");
            try
            {
                var add = await Git(repo, filters, ct, "worktree", "add", "--detach", temp, request.MergeCommitSha!);
                if (!Succeeded(add))
                    return (false, null, "Could not prepare a detached worktree to revert the landing commit.");
                var revert = await Git(temp, filters, ct,
                    "-c", "user.name=ProjectBeacon",
                    "-c", "user.email=projectbeacon-agent@localhost",
                    "-c", "commit.gpgsign=false",
                    "-c", "core.editor=true",
                    "revert", "--no-edit", "-m", "1", request.MergeCommitSha!);
                if (!Succeeded(revert))
                    return (false, null, "Git could not revert the landing commit in a detached worktree.");
                var resultHead = await Git(temp, filters, ct, "rev-parse", "--verify", "HEAD");
                if (!Succeeded(resultHead) || !IsGitSha(resultHead.StdOut.Trim()))
                    return (false, null, "The revert ran but its commit SHA could not be verified.");
                revertCommitSha = resultHead.StdOut.Trim();
                var update = await Git(repo, filters, ct, "update-ref", branchRef, revertCommitSha, request.MergeCommitSha!);
                if (!Succeeded(update))
                    return (false, null, "The target branch advanced before the atomic revert update; the branch was not moved.");
            }
            finally
            {
                await RemoveTemporaryWorktreeAsync(repo, temp, filters);
            }
        }

        return (true, JsonSerializer.Serialize(new
        {
            status = "reverted",
            targetBranch,
            mergeCommitSha = request.MergeCommitSha,
            revertCommitSha
        }), null);
    }

    private static async Task<CandidateResult> BuildCandidateAsync(
        AgentTaskWorktree workspace, Request request, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var targetBranch = workspace.BaseBranch!;
        var branchCheck = await Git(repo, filters, ct, "check-ref-format", "--branch", targetBranch);
        if (!Succeeded(branchCheck))
            return new CandidateResult("failed", ErrorCode: "InvalidTargetBranch");

        var target = await ResolveTargetHeadAsync(workspace, request.LandingAttemptId, filters, ct);
        if (target.Error is not null)
            return new CandidateResult("failed", ErrorCode: target.ErrorCode ?? "TargetUnavailable");

        var temp = CreateTemporaryWorktreePath(request.LandingAttemptId, "candidate");
        try
        {
            var add = await Git(repo, filters, ct, "worktree", "add", "--detach", temp, target.Sha!);
            if (!Succeeded(add))
                return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "CandidateWorktreeFailed");

            var merge = await Git(temp, filters, ct,
                "-c", "user.name=ProjectBeacon",
                "-c", "user.email=projectbeacon-agent@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true",
                "merge", "--no-commit", "--no-ff", "--no-edit", "--no-verify", request.SourceSha);
            if (!Succeeded(merge))
            {
                var conflicts = await ReadConflictsAsync(temp, filters, ct);
                return conflicts.Count > 0
                    ? new CandidateResult("conflicted", targetBranch, target.Sha, Conflicts: conflicts)
                    : new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "MergeCandidateFailed");
            }

            var tree = await Git(temp, filters, ct, "write-tree");
            if (!Succeeded(tree) || !IsGitSha(tree.StdOut.Trim()))
                return new CandidateResult("failed", targetBranch, target.Sha, ErrorCode: "CandidateTreeFailed");
            var treeSha = tree.StdOut.Trim();

            var treeEntries = await Git(temp, filters, ct, "ls-tree", "-r", treeSha);
            if (!Succeeded(treeEntries))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateTreeInspectionFailed");
            if (treeEntries.StdOut.Split('\n').Any(line =>
                line.StartsWith("120000 blob ", StringComparison.Ordinal)
                || line.StartsWith("160000 commit ", StringComparison.Ordinal)))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "SymlinkOrSubmoduleNotAllowed");

            var scope = await CheckCandidateScopeAsync(temp, target.Sha!, filters, ct);
            if (scope.Error is not null)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: scope.Error);
            if (scope.ProtectedPath)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "ProtectedPathChanged");
            if (scope.FileCount > MaxChangedFiles || scope.ChangedLines > MaxChangedLines)
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateDiffTooLarge");

            var relativeProject = Path.GetRelativePath(workspace.RecoveryPath, workspace.WorkingDirectory);
            var checkDirectory = relativeProject == "."
                ? temp
                : Path.GetFullPath(Path.Combine(temp, relativeProject));
            if (!IsWithin(temp, checkDirectory) || !Directory.Exists(checkDirectory))
                return new CandidateResult("failed", targetBranch, target.Sha, treeSha, ErrorCode: "CandidateProjectMissing");

            var check = EvalCheck.Execute(checkDirectory, request.CheckCommand);
            var outputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(check.Output ?? string.Empty))).ToLowerInvariant();
            var workingDiff = await Git(temp, filters, ct, "diff", "--quiet");
            var untracked = await Git(temp, filters, ct, "ls-files", "--others", "--exclude-standard");
            var afterTree = await Git(temp, filters, ct, "write-tree");
            if (workingDiff.ExitCode != 0 || workingDiff.TimedOut
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
        AgentTaskWorktree workspace, Guid attemptId, string[] filters, CancellationToken ct)
    {
        var repo = workspace.RepositoryRoot;
        var targetBranch = workspace.BaseBranch!;
        var checkout = await FindTargetCheckoutAsync(repo, targetBranch, filters, ct);
        if (checkout.Error is not null)
            return (null, checkout.Error, "TargetCheckoutUnavailable");
        if (checkout.Checkout!.CheckedOut)
        {
            var status = await Git(checkout.Checkout.Path, filters, ct, "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(status) || !string.IsNullOrWhiteSpace(status.StdOut))
                return (null, "The target checkout is not clean.", "TargetCheckoutDirty");
            var head = await Git(checkout.Checkout.Path, filters, ct, "rev-parse", "--verify", "HEAD");
            return Succeeded(head) && IsGitSha(head.StdOut.Trim())
                ? (head.StdOut.Trim(), null, null)
                : (null, "Could not read target checkout HEAD.", "TargetHeadUnavailable");
        }

        var remote = await Git(repo, filters, ct, "remote", "get-url", "origin");
        if (Succeeded(remote) && !string.IsNullOrWhiteSpace(remote.StdOut))
        {
            var url = remote.StdOut.Trim();
            if (!AgentTaskWorktree.IsSupportedFetchRemote(url))
                return (null, "The configured origin uses an unsupported Git transport.", "UnsupportedRemote");
            var targetRef = $"refs/beacon/landing/{attemptId:N}/target";
            var fetch = await Git(repo, filters, ct,
                "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
                "--", url, $"+refs/heads/{targetBranch}:{targetRef}");
            if (!Succeeded(fetch))
                return (null, "Could not fetch the daemon-selected target branch.", "TargetFetchFailed");
            var fetched = await Git(repo, filters, ct, "rev-parse", "--verify", targetRef);
            return Succeeded(fetched) && IsGitSha(fetched.StdOut.Trim())
                ? (fetched.StdOut.Trim(), null, null)
                : (null, "Could not verify the fetched target branch.", "TargetHeadUnavailable");
        }
        if (remote.ExitCode is not (0 or 1) || remote.TimedOut)
            return (null, "Could not inspect the origin remote safely.", "TargetRemoteInspectionFailed");
        var local = await Git(repo, filters, ct, "rev-parse", "--verify", $"refs/heads/{targetBranch}");
        return Succeeded(local) && IsGitSha(local.StdOut.Trim())
            ? (local.StdOut.Trim(), null, null)
            : (null, "The target branch is not checked out and no local target branch is available.", "TargetUnavailable");
    }

    private static async Task<(string? Error, int FileCount, int ChangedLines, bool ProtectedPath)> CheckCandidateScopeAsync(
        string candidateWorktree, string targetHeadSha, string[] filters, CancellationToken ct)
    {
        var files = await Git(candidateWorktree, filters, ct, "diff", "--cached", "--name-only", "-z", targetHeadSha);
        if (!Succeeded(files))
            return ("CandidateDiffInspectionFailed", 0, 0, false);
        var paths = files.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        bool Protected(string path)
        {
            var value = path.Replace('\\', '/');
            return value.Equals("SECURITY.md", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                || value.Equals(".github/workflows", StringComparison.OrdinalIgnoreCase)
                || value.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
        }
        var protectedPath = paths.Any(Protected);
        var numstat = await Git(candidateWorktree, filters, ct, "diff", "--cached", "--numstat", targetHeadSha);
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
        return (null, paths.Length, changedLines, protectedPath);
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

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

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
            var sourceSha = root["sourceSha"]?.GetValue<string>();
            var hash = root["checkCommandHash"]?.GetValue<string>();
            var checkCommand = root["checkCommand"]?.GetValue<string>();
            if (action is not ("Preview" or "Execute" or "Revert")
                || !IsGitSha(sourceSha)
                || hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(checkCommand) || checkCommand.Length > 2000)
            {
                error = "GitLand payload action, source SHA, or trusted check command is invalid.";
                return false;
            }
            request = new Request(
                projectId, taskId, attemptId, action, sourceSha!, hash!, checkCommand!,
                root["targetHeadSha"]?.GetValue<string>(),
                root["targetTreeSha"]?.GetValue<string>(),
                root["mergeCommitSha"]?.GetValue<string>());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "GitLand payload is malformed.";
            return false;
        }
    }

    private static string HashCommand(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Clip(string? value)
    {
        var output = value?.Trim() ?? string.Empty;
        return output.Length <= 12_000 ? output : output[..12_000] + "\n[output truncated]";
    }
}
