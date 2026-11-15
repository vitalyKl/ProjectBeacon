namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client;

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
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, commandHash, checkCommand),
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
                Payload(projectId, taskId, attemptId, "Execute", sourceSha, commandHash, checkCommand, targetHead, candidateTree),
                settings, TestContext.Current.CancellationToken);
            Assert.True(execute.Ok, execute.Error ?? execute.Result);
            using var landedJson = JsonDocument.Parse(execute.Result!);
            Assert.Equal("landed", landedJson.RootElement.GetProperty("status").GetString());
            var mergeCommit = landedJson.RootElement.GetProperty("mergeCommitSha").GetString()!;
            Assert.Equal(0, landedJson.RootElement.GetProperty("postMergeCheckExitCode").GetInt32());
            Assert.Matches(
                "^[0-9a-f]{64}$",
                landedJson.RootElement.GetProperty("postMergeCheckOutputHash").GetString() ?? string.Empty);
            Assert.Equal(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.Equal(candidateTree, await GitAsync(repository, "rev-parse", "HEAD^{tree}"));
            Assert.Equal("reviewed task change\n",
                await File.ReadAllTextAsync(Path.Combine(projectRoot, "task-change.txt")));

            var revert = await GitLandExecutor.ExecuteAsync(
                projectRoot, projectId,
                Payload(projectId, taskId, attemptId, "Revert", sourceSha, commandHash, null, mergeCommitSha: mergeCommit),
                settings, TestContext.Current.CancellationToken);
            Assert.True(revert.Ok, revert.Error ?? revert.Result);
            using var revertedJson = JsonDocument.Parse(revert.Result!);
            Assert.Equal("reverted", revertedJson.RootElement.GetProperty("status").GetString());
            Assert.NotEqual(mergeCommit, await GitAsync(repository, "rev-parse", "refs/heads/main"));
            Assert.False(File.Exists(Path.Combine(projectRoot, "task-change.txt")));
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain", "--untracked-files=all"));
        }
        finally
        {
            try { await GitAsync(repository, "worktree", "prune"); } catch { }
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
        var opened = await AgentTaskWorktree.CreateAsync(
            projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
        Assert.NotNull(opened.Worktree);
        await using (var taskWorktree = opened.Worktree!)
        {
            await File.WriteAllTextAsync(Path.Combine(taskWorktree.WorkingDirectory, "shared.txt"), "task version\n");
            var commit = await taskWorktree.CommitChangesAsync(
                "ProjectBeacon: conflicting task change", CancellationToken.None);
            Assert.True(commit.Success, commit.Error);
            sourceSha = commit.CommitSha!;
        }

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "shared.txt"), "target version\n");
        await GitAsync(repository, "add", "src/app/shared.txt");
        await GitAsync(repository, "commit", "-m", "Conflicting target change");
        var originalTargetHead = await GitAsync(repository, "rev-parse", "HEAD");
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
                Payload(projectId, taskId, attemptId, "Preview", sourceSha, commandHash, checkCommand),
                settings, TestContext.Current.CancellationToken);
            Assert.True(result.Ok, result.Error ?? result.Result);
            using var document = JsonDocument.Parse(result.Result!);
            Assert.Equal("conflicted", document.RootElement.GetProperty("status").GetString());
            Assert.Contains(
                "src/app/shared.txt",
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

    private static string Payload(
        Guid projectId,
        Guid taskId,
        Guid attemptId,
        string action,
        string sourceSha,
        string checkCommandHash,
        string? checkCommand,
        string? targetHeadSha = null,
        string? targetTreeSha = null,
        string? mergeCommitSha = null) =>
        JsonSerializer.Serialize(new
        {
            projectId,
            taskId,
            landingAttemptId = attemptId,
            action,
            sourceSha,
            checkCommandHash,
            checkCommand,
            targetHeadSha,
            targetTreeSha,
            mergeCommitSha
        });
}
