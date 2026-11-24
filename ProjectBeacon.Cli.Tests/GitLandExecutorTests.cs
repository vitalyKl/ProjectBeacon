namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed partial class AgentTaskGitOperationsTests
{
    [Fact]
    public async Task GitLand_PreviewExecuteAndRevert_UsesExactReviewedCandidate()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        string reviewTreeSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: commit reviewed task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");

        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };
        try
        {
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());
            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;
            Assert.Equal(0, previewJson.RootElement.GetProperty("checkExitCode").GetInt32());
            var checkOutputHash = previewJson.RootElement.GetProperty("checkOutputHash").GetString();
            Assert.Matches("^[0-9a-f]{64}$", checkOutputHash ?? string.Empty);
            Assert.Equal(targetHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landedJson = JsonDocument.Parse(execute.Result!);
            Assert.True(
                string.Equals("landed", landedJson.RootElement.GetProperty("status").GetString(), StringComparison.Ordinal),
                execute.Result);
            var mergeCommit = landedJson.RootElement.GetProperty("mergeCommitSha").GetString()!;
            var taskMetadataPath = Path.Combine(
                worktreeRoot, projectId.ToString("N"), "beacon", "task", taskId.ToString("N") + ".beacon.json");
            using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(taskMetadataPath)))
            {
                Assert.Equal("main", metadata.RootElement.GetProperty("landingTargetBranch").GetString());
                Assert.Equal(sourceSha, metadata.RootElement.GetProperty("landingSourceSha").GetString());
                Assert.Equal(mergeCommit, metadata.RootElement.GetProperty("landingCommitSha").GetString());
                Assert.True(metadata.RootElement.GetProperty("landedAtUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
            }
            Assert.Equal(0, landedJson.RootElement.GetProperty("postMergeCheckExitCode").GetInt32());
            Assert.Matches(
                "^[0-9a-f]{64}$",
                landedJson.RootElement.GetProperty("postMergeCheckOutputHash").GetString() ?? string.Empty);
            Assert.Equal(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(candidateTree, await GitAsync(repository, "rev-parse", "HEAD^{tree}"));
            Assert.Equal("reviewed task change\n",
                await File.ReadAllTextAsync(Path.Combine(projectRoot, "task-change.txt")));

            // A duplicate Execute after a lost completion must reconcile the durable marker
            // and return the same merge commit, never construct a second merge.
            var executeRetry = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(executeRetry.Ok, executeRetry.Error ?? executeRetry.Result);
            using var retriedLanding = JsonDocument.Parse(executeRetry.Result!);
            Assert.Equal("landed", retriedLanding.RootElement.GetProperty("status").GetString());
            Assert.Equal(mergeCommit, retriedLanding.RootElement.GetProperty("mergeCommitSha").GetString());
            Assert.Equal(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, reviewTreeSha, commandHash, null, mergeCommitSha: mergeCommit),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var revertedJson = JsonDocument.Parse(revert.Result!);
            Assert.Equal("reverted", revertedJson.RootElement.GetProperty("status").GetString());
            var revertCommit = revertedJson.RootElement.GetProperty("revertCommitSha").GetString();
            Assert.NotEqual(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.False(File.Exists(Path.Combine(projectRoot, "task-change.txt")));

            // A duplicate Revert after a lost completion must reconcile the same revert marker.
            var revertRetry = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, reviewTreeSha, commandHash, null, mergeCommitSha: mergeCommit),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revertRetry.Ok, revertRetry.Error ?? revertRetry.Result);
            using var retriedRevert = JsonDocument.Parse(revertRetry.Result!);
            Assert.Equal("reverted", retriedRevert.RootElement.GetProperty("status").GetString());
            Assert.Equal(revertCommit, retriedRevert.RootElement.GetProperty("revertCommitSha").GetString());
            Assert.Equal(revertCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TaskWorktreeCommit_DetectsContentOnlyChangesToTrackedFiles()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            var baseSha = await GitAsync(worktree.RecoveryPath, "rev-parse", "HEAD");
            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"),
                "content-only task edit\n");

            var committed = await worktree.CommitChangesAsync(
                "ProjectBeacon: update tracked file content", CancellationToken.None);

            Assert.True(committed.Success, committed.Error);
            Assert.NotNull(committed.CommitSha);
            Assert.NotEqual(baseSha, committed.CommitSha);
            Assert.Contains(
                "src/app/shared.txt",
                await GitAsync(worktree.RecoveryPath, "show", "--format=", "--name-only", "HEAD"),
                StringComparison.Ordinal);
            Assert.Equal(
                string.Empty,
                await GitAsync(worktree.RecoveryPath, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewConflict_ReportsPathsWithoutChangingTarget()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        string reviewTreeSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        var taskWorkingDirectory = opened.Worktree!.WorkingDirectory;
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(Path.Combine(taskWorktree.WorkingDirectory, "shared.txt"), "task version\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: conflicting task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        Assert.Equal(string.Empty, await GitAsync(
            taskWorkingDirectory, "status", "--porcelain", "--untracked-files=all"));

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "shared.txt"), "target version\n");
        await GitAsync(repository, "add", "src/app/shared.txt");
        await GitAsync(repository, "commit", "-m", "Conflicting target change");
        var originalTargetHead = await GitAsync(repository, "rev-parse", "HEAD");
        Assert.Equal(
            string.Empty,
            await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        Assert.NotEqual(
            await GitAsync(repository, "rev-parse", "main"),
            sourceSha);
        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };

        try
        {
            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("conflicted", document.RootElement.GetProperty("status").GetString());
            Assert.Contains(
                "shared.txt",
                document.RootElement.GetProperty("conflicts").EnumerateArray().Select(item => item.GetString() ?? string.Empty));
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "HEAD"));
            Assert.Equal("target version\n", await File.ReadAllTextAsync(Path.Combine(projectRoot, "shared.txt")));
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewRejectsChangesOutsideAuthorizedProjectDirectory()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        string reviewTreeSha;

        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "authorized-change.txt"),
                "inside project\n");
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.RecoveryPath, "outside-project.txt"),
                "outside project\n");
            await GitAsync(taskWorktree.RecoveryPath, "add", "--", ".");
            await GitAsync(taskWorktree.RecoveryPath, "commit", "-m", "ProjectBeacon: deliberately out-of-scope task commit");
            sourceSha = await GitAsync(taskWorktree.RecoveryPath, "rev-parse", "HEAD");
        }
        reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");

        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };
        try
        {
            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);

            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("CandidateChangesOutsideProject",
                document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(await GitAsync(repository, "rev-parse", "HEAD"),
                await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewRejectsRenameThatRemovesProtectedSecurityFile()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "SECURITY.md"), "protected security policy\\n");
        await GitAsync(repository, "add", "src/app/SECURITY.md");
        await GitAsync(repository, "commit", "-m", "Add protected security policy");

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        AgentTaskWorktree? taskWorktree = null;
        try
        {
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            taskWorktree = opened.Worktree!;

            await GitAsync(taskWorktree.RecoveryPath, "mv",
                "src/app/SECURITY.md", "src/app/README-public.md");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: rename protected security file", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            var sourceSha = commit.CommitSha!;
            var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
            await taskWorktree.DisposeAsync();
            taskWorktree = null;
            const string checkCommand = "echo review-ok";
            var commandHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
            var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };

            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);

            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("ProtectedPathChanged", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            if (taskWorktree is not null)
                await taskWorktree.DisposeAsync();
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewRejectsRenameIntoProjectFromOutsideProject()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        await File.WriteAllTextAsync(Path.Combine(repository, "outside-project.txt"), "outside project content\\n");
        await GitAsync(repository, "add", "outside-project.txt");
        await GitAsync(repository, "commit", "-m", "Add file outside the project");

        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        AgentTaskWorktree? taskWorktree = null;
        try
        {
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            taskWorktree = opened.Worktree!;

            await GitAsync(taskWorktree.RecoveryPath, "mv",
                "outside-project.txt", "src/app/moved-into-project.txt");
            // Deliberately create a malformed candidate that bypasses the project-scoped
            // git_commit tool. GitLand must enforce the boundary independently at landing time.
            await GitAsync(taskWorktree.RecoveryPath, "add", "-A");
            await GitAsync(taskWorktree.RecoveryPath, "commit", "-m",
                "ProjectBeacon: deliberately move an out-of-scope file into project");
            var sourceSha = await GitAsync(taskWorktree.RecoveryPath, "rev-parse", "HEAD");
            var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
            await taskWorktree.DisposeAsync();
            taskWorktree = null;
            const string checkCommand = "echo review-ok";
            var commandHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
            var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };

            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);

            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("CandidateChangesOutsideProject", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            if (taskWorktree is not null)
                await taskWorktree.DisposeAsync();
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_ExecuteRejectsTargetAdvancedSincePreview()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        string reviewTreeSha;

        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };
        try
        {
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "target-change.txt"), "advanced target\n");
            await GitAsync(repository, "add", "src/app/target-change.txt");
            await GitAsync(repository, "commit", "-m", "Advance target after preview");
            var advancedTarget = await GitAsync(repository, "rev-parse", "HEAD");

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var failedJson = JsonDocument.Parse(execute.Result!);
            Assert.Equal("failed", failedJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("TargetChangedAfterPreview", failedJson.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(advancedTarget, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_UncheckedOutTarget_CasUsesLaggingLocalHeadWhenOriginAdvanced()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        var remoteClone = Path.Combine(root, "remote-clone");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
        var origin = repository + "-origin.git";
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "lagging-target-task-change.txt"),
                "reviewed task change\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: commit task change against origin main", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }

        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var originalTargetHead = await GitAsync(repository, "rev-parse", "refs/heads/main");

        // Leave main checked out nowhere. Advance origin/main from a separate clone while
        // the workstation's local refs/heads/main deliberately remains at originalTargetHead.
        await GitAsync(repository, "branch", "workstation-checkout");
        await GitAsync(repository, "checkout", "workstation-checkout");
        await GitAsync(root, "clone", "--branch", "main", "--single-branch", origin, remoteClone);
        await GitAsync(remoteClone, "config", "user.name", "Remote Target");
        await GitAsync(remoteClone, "config", "user.email", "remote-target@projectbeacon.local");
        var remoteTargetFile = Path.Combine(remoteClone, "src", "app", "remote-target-change.txt");
        await File.WriteAllTextAsync(remoteTargetFile, "newer remote target\n");
        await GitAsync(remoteClone, "add", "-A");
        await GitAsync(remoteClone, "commit", "-m", "advance origin main");
        await GitAsync(remoteClone, "push", "origin", "main");
        var remoteTargetHead = await GitAsync(remoteClone, "rev-parse", "HEAD");
        Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
        Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "HEAD"));
        var remoteTargetTree = await GitAsync(remoteClone, "rev-parse", "HEAD^{tree}");

        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };

        try
        {
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());
            var previewTargetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;
            Assert.Equal(remoteTargetHead, previewTargetHead);
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "HEAD"));

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha,
                    commandHash, checkCommand, previewTargetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landed = JsonDocument.Parse(execute.Result!);
            Assert.Equal("landed", landed.RootElement.GetProperty("status").GetString());
            var mergeCommitSha = landed.RootElement.GetProperty("mergeCommitSha").GetString()!;
            Assert.Equal(mergeCommitSha, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(remoteTargetHead,
                await GitAsync(repository, "rev-parse", mergeCommitSha + "^1"));
            Assert.Equal(sourceSha,
                await GitAsync(repository, "rev-parse", mergeCommitSha + "^2"));
            Assert.Equal(candidateTree,
                await GitAsync(repository, "rev-parse", mergeCommitSha + "^{tree}"));
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "HEAD"));
            Assert.Equal("workstation-checkout",
                await GitAsync(repository, "symbolic-ref", "--short", "HEAD"));

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, reviewTreeSha,
                    commandHash, null, mergeCommitSha: mergeCommitSha),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var reverted = JsonDocument.Parse(revert.Result!);
            Assert.Equal("reverted", reverted.RootElement.GetProperty("status").GetString());
            var revertCommitSha = reverted.RootElement.GetProperty("revertCommitSha").GetString()!;
            Assert.Equal(revertCommitSha, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(mergeCommitSha,
                await GitAsync(repository, "rev-parse", revertCommitSha + "^"));
            Assert.Equal(remoteTargetTree,
                await GitAsync(repository, "rev-parse", revertCommitSha + "^{tree}"));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "ls-tree", "-r", revertCommitSha, "--", "src/app/lagging-target-task-change.txt"));
            Assert.Contains("remote-target-change.txt",
                await GitAsync(repository, "ls-tree", revertCommitSha, "--", "src/app/remote-target-change.txt"),
                StringComparison.Ordinal);
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "HEAD"));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_UncheckedOutTarget_UsesTemporaryBranchWorktreeForExecuteAndRevert()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        string reviewTreeSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "unchecked-target-change.txt"),
                "landed through temporary target worktree\\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed change for unchecked target", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");

        // Move the workstation's real checkout off main after its persistent task
        // worktree has recorded main as its daemon-selected landing target.
        await GitAsync(repository, "branch", "workstation-checkout");
        await GitAsync(repository, "checkout", "workstation-checkout");
        Assert.Equal("workstation-checkout", await GitAsync(repository, "symbolic-ref", "--short", "HEAD"));
        var originalWorkstationHead = await GitAsync(repository, "rev-parse", "HEAD");

        var checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };
        try
        {
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());
            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;
            Assert.Equal(originalWorkstationHead, await GitAsync(repository, "rev-parse", "HEAD"));

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landed = JsonDocument.Parse(execute.Result!);
            Assert.True(
                string.Equals("landed", landed.RootElement.GetProperty("status").GetString(), StringComparison.Ordinal),
                execute.Result);
            var mergeCommitSha = landed.RootElement.GetProperty("mergeCommitSha").GetString()!;
            Assert.Equal(mergeCommitSha, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(originalWorkstationHead, await GitAsync(repository, "rev-parse", "HEAD"));
            Assert.Equal("workstation-checkout", await GitAsync(repository, "symbolic-ref", "--short", "HEAD"));
            Assert.DoesNotContain(
                "branch refs/heads/main",
                await GitAsync(repository, "worktree", "list", "--porcelain"),
                StringComparison.Ordinal);

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, reviewTreeSha, commandHash, null, mergeCommitSha: mergeCommitSha),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var reverted = JsonDocument.Parse(revert.Result!);
            Assert.Equal("reverted", reverted.RootElement.GetProperty("status").GetString());
            var revertCommitSha = reverted.RootElement.GetProperty("revertCommitSha").GetString()!;
            Assert.Equal(revertCommitSha, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(originalWorkstationHead, await GitAsync(repository, "rev-parse", "HEAD"));
            Assert.Equal("workstation-checkout", await GitAsync(repository, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
            Assert.DoesNotContain(
                "branch refs/heads/main",
                await GitAsync(repository, "worktree", "list", "--porcelain"),
                StringComparison.Ordinal);
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_LandViaIntegrationPreviewAndExecute_PreserveDirtyBaseCheckout()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n");
            var taskCommit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", CancellationToken.None);
            Assert.True(taskCommit.Success, taskCommit.Error);
            sourceSha = taskCommit.CommitSha!;
        }

        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var originalBaseHead = await GitAsync(repository, "rev-parse", "refs/heads/main");
        const string checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };
        var uncommittedPath = Path.Combine(projectRoot, "uncommitted-target.txt");

        try
        {
            await File.WriteAllTextAsync(uncommittedPath, "keep this local change\n");
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(
                    projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha,
                    commandHash, checkCommand,
                    reviewBaseBranch: "main",
                    allowIntegrationFallback: true),
                settings, TestContext.Current.CancellationToken);

            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("beacon/integration", previewJson.RootElement.GetProperty("targetBranch").GetString());
            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;
            Assert.Equal(originalBaseHead, targetHead);
            Assert.Equal(originalBaseHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal("keep this local change\n", await File.ReadAllTextAsync(uncommittedPath));

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(
                    projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha,
                    commandHash, checkCommand, targetHead, candidateTree,
                    targetBranch: "beacon/integration",
                    reviewBaseBranch: "main"),
                settings, TestContext.Current.CancellationToken);

            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landed = JsonDocument.Parse(execute.Result!);
            Assert.Equal("landed", landed.RootElement.GetProperty("status").GetString());
            var mergeCommit = landed.RootElement.GetProperty("mergeCommitSha").GetString()!;
            Assert.Equal(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/beacon/integration"));
            Assert.Equal(originalBaseHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(originalBaseHead, await GitAsync(projectRoot, "rev-parse", "HEAD"));
            Assert.Equal("keep this local change\n", await File.ReadAllTextAsync(uncommittedPath));
            Assert.Contains(
                "?? src/app/uncommitted-target.txt",
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"),
                StringComparison.Ordinal);
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_LandViaIntegration_UsesExistingLocalIntegrationBranchWhenOriginHasNoIntegrationRef()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await GitAsync(repository, "branch", "beacon/integration");
        await GitAsync(repository, "checkout", "beacon/integration");
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "integration-change.txt"),
            "previous local landing\n");
        await GitAsync(repository, "add", "-A");
        await GitAsync(repository, "commit", "-m", "ProjectBeacon: seed local integration branch");
        var integrationBaseHead = await GitAsync(repository, "rev-parse", "refs/heads/beacon/integration");
        await GitAsync(repository, "checkout", "main");

        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n");
            var taskCommit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", CancellationToken.None);
            Assert.True(taskCommit.Success, taskCommit.Error);
            sourceSha = taskCommit.CommitSha!;
        }

        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var originalMainHead = await GitAsync(repository, "rev-parse", "refs/heads/main");
        const string checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings
        {
            WorkspaceMode = "Branch",
            WorktreeRoot = worktreeRoot
        };
        var uncommittedPath = Path.Combine(projectRoot, "uncommitted-target.txt");

        try
        {
            await File.WriteAllTextAsync(uncommittedPath, "preserve the local change\n");
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(
                    projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha,
                    commandHash, checkCommand,
                    reviewBaseBranch: "main",
                    allowIntegrationFallback: true),
                settings, TestContext.Current.CancellationToken);

            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("beacon/integration", previewJson.RootElement.GetProperty("targetBranch").GetString());
            Assert.Equal(integrationBaseHead, previewJson.RootElement.GetProperty("targetHeadSha").GetString());

            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;
            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(
                    projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha,
                    commandHash, checkCommand, targetHead, candidateTree,
                    targetBranch: "beacon/integration",
                    reviewBaseBranch: "main"),
                settings, TestContext.Current.CancellationToken);

            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landed = JsonDocument.Parse(execute.Result!);
            Assert.Equal("landed", landed.RootElement.GetProperty("status").GetString());
            var mergeCommit = landed.RootElement.GetProperty("mergeCommitSha").GetString()!;
            Assert.Equal(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/beacon/integration"));
            Assert.Equal(originalMainHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal("previous local landing",
                await GitAsync(repository, "show", "refs/heads/beacon/integration:src/app/integration-change.txt"));
            Assert.Equal("reviewed task change",
                await GitAsync(repository, "show", "refs/heads/beacon/integration:src/app/task-change.txt"));
            Assert.Equal("preserve the local change\n", await File.ReadAllTextAsync(uncommittedPath));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewRejectsDirtyCheckedOutTargetWithoutChangingIt()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var originalTargetHead = await GitAsync(repository, "rev-parse", "refs/heads/main");
        const string checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };
        var uncommittedPath = Path.Combine(projectRoot, "uncommitted-target.txt");

        try
        {
            await File.WriteAllTextAsync(uncommittedPath, "keep this local change\n");
            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);

            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("TargetCheckoutDirty", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal("keep this local change\n", await File.ReadAllTextAsync(uncommittedPath));
            Assert.Contains(
                "?? src/app/uncommitted-target.txt",
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"),
                StringComparison.Ordinal);
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewRejectsProtectedProjectManifestChanges()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "Beacon.Tests.csproj"),
                "<Project />\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: add a build manifest", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        var originalTargetHead = await GitAsync(repository, "rev-parse", "refs/heads/main");
        const string checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };

        try
        {
            var result = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);

            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("ProtectedPathChanged", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(originalTargetHead, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_RevertRejectsTargetAdvancedAfterLanding()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        string sourceSha;
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }
        var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
        const string checkCommand = "echo review-ok";
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var settings = new WorkstationSettings { WorkspaceMode = "Branch", WorktreeRoot = worktreeRoot };

        try
        {
            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            var targetHead = previewJson.RootElement.GetProperty("targetHeadSha").GetString()!;
            var candidateTree = previewJson.RootElement.GetProperty("candidateTreeSha").GetString()!;

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, reviewTreeSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landed = JsonDocument.Parse(execute.Result!);
            Assert.True(
                string.Equals("landed", landed.RootElement.GetProperty("status").GetString(), StringComparison.Ordinal),
                execute.Result);
            var mergeCommit = landed.RootElement.GetProperty("mergeCommitSha").GetString()!;

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "later-target-change.txt"), "keep later work\n");
            await GitAsync(repository, "add", "src/app/later-target-change.txt");
            await GitAsync(repository, "commit", "-m", "Commit after ProjectBeacon landing");
            var advancedTarget = await GitAsync(repository, "rev-parse", "refs/heads/main");

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, reviewTreeSha, commandHash, null, mergeCommitSha: mergeCommit),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var rejected = JsonDocument.Parse(revert.Result!);
            Assert.Equal("failed", rejected.RootElement.GetProperty("status").GetString());
            Assert.Equal("TargetAdvancedBeyondLanding", rejected.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(advancedTarget, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.True(File.Exists(Path.Combine(projectRoot, "task-change.txt")));
            Assert.Equal("keep later work\n",
                await File.ReadAllTextAsync(Path.Combine(projectRoot, "later-target-change.txt")));
            Assert.Equal(string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_PreviewEnforcesProjectDiffLimitsAndAdditionalProtectedPaths()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        AgentTaskWorktree? taskWorktree = null;
        try
        {
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            taskWorktree = opened.Worktree!;

            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, "policy-file-one.txt"),
                "one\n");
            Directory.CreateDirectory(Path.Combine(taskWorktree.WorkingDirectory, ".beacon"));
            await File.WriteAllTextAsync(
                Path.Combine(taskWorktree.WorkingDirectory, ".beacon", "release.json"),
                "{}\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: create files for policy-bound landing", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            var sourceSha = commit.CommitSha!;
            var reviewTreeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
            await taskWorktree.DisposeAsync();
            taskWorktree = null;

            const string checkCommand = "echo review-ok";
            var commandHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
            var settings = new WorkstationSettings
            {
                WorkspaceMode = "Branch",
                WorktreeRoot = worktreeRoot
            };

            var tooLarge = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, reviewTreeSha,
                    commandHash, checkCommand, maxChangedFiles: 1),
                settings, TestContext.Current.CancellationToken);
            Assert.True(tooLarge.Ok, tooLarge.Error ?? tooLarge.Result);
            using var tooLargeResult = JsonDocument.Parse(tooLarge.Result!);
            Assert.Equal("failed", tooLargeResult.RootElement.GetProperty("status").GetString());
            Assert.Equal("CandidateDiffTooLarge", tooLargeResult.RootElement.GetProperty("errorCode").GetString());

            var protectedPath = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, Guid.NewGuid(), "Preview", sourceSha, reviewTreeSha,
                    commandHash, checkCommand,
                    maxChangedFiles: 10,
                    protectedPaths: ["policy-file-one.txt"]),
                settings, TestContext.Current.CancellationToken);
            Assert.True(protectedPath.Ok, protectedPath.Error ?? protectedPath.Result);
            using var protectedResult = JsonDocument.Parse(protectedPath.Result!);
            Assert.Equal("failed", protectedResult.RootElement.GetProperty("status").GetString());
            Assert.Equal("ProtectedPathChanged", protectedResult.RootElement.GetProperty("errorCode").GetString());

            var releaseManifest = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, Guid.NewGuid(), "Preview", sourceSha, reviewTreeSha,
                    commandHash, checkCommand, maxChangedFiles: 10),
                settings, TestContext.Current.CancellationToken);
            Assert.True(releaseManifest.Ok, releaseManifest.Error ?? releaseManifest.Result);
            using var releaseManifestResult = JsonDocument.Parse(releaseManifest.Result!);
            Assert.Equal("failed", releaseManifestResult.RootElement.GetProperty("status").GetString());
            Assert.Equal("ProtectedPathChanged", releaseManifestResult.RootElement.GetProperty("errorCode").GetString());

            Assert.Equal(
                string.Empty,
                await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
            Assert.Equal(
                await GitAsync(repository, "rev-parse", "main"),
                await GitAsync(repository, "rev-parse", "HEAD"));
        }
        finally
        {
            if (taskWorktree is not null)
                await taskWorktree.DisposeAsync();
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitLand_ExecuteAndRevertOnGitHubOriginRequirePullRequestLanding()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
        await GitAsync(repository, "remote", "add", "origin", "https://github.com/acme/project-beacon.git");
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        AgentTaskWorktree? worktree = null;
        try
        {
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, TestContext.Current.CancellationToken);
            Assert.NotNull(opened.Worktree);
            worktree = opened.Worktree!;

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "task-change.txt"),
                "reviewed task change\n",
                TestContext.Current.CancellationToken);
            var committed = await worktree.CommitChangesAsync(
                "ProjectBeacon: reviewed task change", TestContext.Current.CancellationToken);
            Assert.True(committed.Success, committed.Error);
            var sourceSha = committed.CommitSha!;
            var treeSha = await GitAsync(repository, "rev-parse", sourceSha + "^{tree}");
            await worktree.DisposeAsync();
            worktree = null;
            var command = "echo review-ok";
            var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
            var settings = new WorkstationSettings
            {
                WorkspaceMode = "Branch",
                WorktreeRoot = worktreeRoot
            };
            var mainBefore = await GitAsync(repository, "rev-parse", "refs/heads/main");

            var preview = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, treeSha, commandHash, command),
                settings, TestContext.Current.CancellationToken);
            Assert.True(preview.Ok, preview.Error ?? preview.Result);
            using var previewJson = JsonDocument.Parse(preview.Result!);
            Assert.Equal("previewed", previewJson.RootElement.GetProperty("status").GetString());

            var execute = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, treeSha, commandHash, command),
                settings, TestContext.Current.CancellationToken);

            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var executeJson = JsonDocument.Parse(execute.Result!);
            Assert.Equal("failed", executeJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("GitHubRemoteRequiresPullRequest", executeJson.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                mainBefore,
                await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, treeSha, commandHash, null,
                    mergeCommitSha: sourceSha),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var revertJson = JsonDocument.Parse(revert.Result!);
            Assert.Equal("failed", revertJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("GitHubRemoteRequiresPullRequest", revertJson.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string Payload(
        Guid projectId,
        Guid taskId,
        Guid attemptId,
        string action,
        string sourceSha,
        string reviewTreeSha,
        string checkCommandHash,
        string? checkCommand,
        string? targetHeadSha = null,
        string? targetTreeSha = null,
        string? mergeCommitSha = null,
        int maxChangedFiles = 200,
        int maxChangedLines = 20_000,
        IReadOnlyList<string>? protectedPaths = null,
        string targetBranch = "main",
        string? reviewBaseBranch = null,
        bool allowIntegrationFallback = false) =>
        JsonSerializer.Serialize(new
        {
            projectId,
            taskId,
            landingAttemptId = attemptId,
            action,
            targetBranch,
            reviewBaseBranch,
            allowIntegrationFallback,
            sourceSha,
            reviewTreeSha,
            checkCommandHash,
            checkCommand,
            targetHeadSha,
            targetTreeSha,
            mergeCommitSha,
            maxChangedFiles,
            maxChangedLines,
            protectedPaths = protectedPaths ?? Array.Empty<string>()
        });
}
