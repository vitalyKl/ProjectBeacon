namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed partial class AgentTaskGitOperationsTests
{
    [Fact]
    public async Task GitCommit_IsScopedToTheProjectDirectoryAndRejectsTraversal()
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
            var authorization = CreateAuthorization(worktree, projectId, taskId);

            var branchInfo = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitInfo, """{"kind":"branches"}""");
            Assert.False(branchInfo.IsError, branchInfo.Text);
            using (var document = JsonDocument.Parse(branchInfo.Text))
            {
                Assert.Equal(worktree.BranchName, document.RootElement.GetProperty("branch").GetString());
                Assert.Equal(worktree.BaseSha, document.RootElement.GetProperty("baseSha").GetString());
            }

            await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "new-file.txt"), "project change\n");
            await File.WriteAllTextAsync(Path.Combine(worktree.RecoveryPath, "outside-project.txt"), "keep uncommitted\n");

            var committed = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit, """{"message":"Add project change"}""");
            Assert.False(committed.IsError, committed.Text);
            using (var document = JsonDocument.Parse(committed.Text))
            {
                Assert.True(document.RootElement.GetProperty("committed").GetBoolean());
                Assert.Matches("^[0-9a-f]{40,64}$", document.RootElement.GetProperty("commitSha").GetString() ?? "");
            }

            var files = await GitAsync(worktree.RecoveryPath, "show", "--format=", "--name-only", "HEAD");
            Assert.Contains("src/app/new-file.txt", files, StringComparison.Ordinal);
            Assert.DoesNotContain("outside-project.txt", files, StringComparison.Ordinal);
            Assert.Contains("outside-project.txt",
                await GitAsync(worktree.RecoveryPath, "status", "--porcelain", "--untracked-files=all"),
                StringComparison.Ordinal);
            var refusedWip = await worktree.CompleteChangesAsync(
                "ProjectBeacon: WIP task boundary test", CancellationToken.None);
            Assert.False(refusedWip.Success);
            Assert.Contains("outside the authorized project directory", refusedWip.Error ?? string.Empty, StringComparison.Ordinal);
            File.Delete(Path.Combine(worktree.RecoveryPath, "outside-project.txt"));

            var traversal = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit,
                """{"message":"Reject path traversal","paths":["../../outside-project.txt"]}""");
            Assert.True(traversal.IsError);
            Assert.Contains("traversal", traversal.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (worktree is not null)
            {
                // Keep cleanup independent from an earlier assertion. A deliberately
                // uncommitted sibling-project file must not make DisposeAsync mask the
                // original test failure while exercising the project boundary guard.
                var outsideProjectFile = Path.Combine(worktree.RecoveryPath, "outside-project.txt");
                if (File.Exists(outsideProjectFile))
                    File.Delete(outsideProjectFile);
                await worktree.DisposeAsync();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitIntegrate_MergeConflictCanBeResolvedAndContinued()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "task branch version\n");
            var commit = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitCommit, """{"message":"Change shared file"}""");
            Assert.False(commit.IsError, commit.Text);

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "shared.txt"), "upstream version\n");
            await GitAsync(repository, "add", "src/app/shared.txt");
            await GitAsync(repository, "commit", "-m", "Change shared file on base");
            await GitAsync(repository, "push", "origin", "main");
            var newBaseSha = await GitAsync(repository, "rev-parse", "HEAD");

            var integrated = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using (var document = JsonDocument.Parse(integrated.Text))
            {
                Assert.Equal("conflict", document.RootElement.GetProperty("status").GetString());
                Assert.Contains("src/app/shared.txt",
                    document.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString() ?? string.Empty));
            }

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "resolved version\n");
            var continued = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitIntegrate, """{"action":"continue"}""");
            Assert.False(continued.IsError, continued.Text);
            using (var document = JsonDocument.Parse(continued.Text))
            {
                Assert.Equal("integrated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal(newBaseSha, document.RootElement.GetProperty("baseSha").GetString());
            }

            Assert.Equal("resolved version\n",
                await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "shared.txt")));
            Assert.Equal(string.Empty,
                await GitAsync(worktree.RecoveryPath, "status", "--porcelain", "--untracked-files=all"));

            var info = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitInfo, """{"kind":"branches"}""");
            Assert.False(info.IsError, info.Text);
            using var branch = JsonDocument.Parse(info.Text);
            Assert.Equal(newBaseSha, branch.RootElement.GetProperty("baseSha").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task GitIntegrate_CanMergeFixedIntegrationBranchWithoutChangingReviewBase()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            var reviewBaseSha = worktree.BaseSha;

            await GitAsync(repository, "switch", "-c", "beacon/integration");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "integration-only.txt"),
                "integration branch contribution\n");
            await GitAsync(repository, "add", "src/app/integration-only.txt");
            await GitAsync(repository, "commit", "-m", "Add integration branch contribution");
            var integrationSha = await GitAsync(repository, "rev-parse", "HEAD");
            await GitAsync(repository, "push", "origin", "beacon/integration");
            await GitAsync(repository, "switch", "main");

            var integrated = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using (var document = JsonDocument.Parse(integrated.Text))
            {
                Assert.Equal("integrated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("integration", document.RootElement.GetProperty("source").GetString());
                Assert.Equal(reviewBaseSha, document.RootElement.GetProperty("baseSha").GetString());
                Assert.Equal("beacon/integration", document.RootElement.GetProperty("integrationBranch").GetString());
                Assert.Equal(integrationSha, document.RootElement.GetProperty("integrationSha").GetString());
            }

            Assert.Equal("integration branch contribution\n",
                await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "integration-only.txt")));
            Assert.Equal(string.Empty,
                await GitAsync(worktree.RecoveryPath, "status", "--porcelain", "--untracked-files=all"));

            var info = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitInfo, """{"kind":"branches"}""");
            Assert.False(info.IsError, info.Text);
            using (var document = JsonDocument.Parse(info.Text))
            {
                Assert.Equal(reviewBaseSha, document.RootElement.GetProperty("baseSha").GetString());
                Assert.Equal(integrationSha, document.RootElement.GetProperty("integrationSha").GetString());
            }

            var repeated = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");
            Assert.False(repeated.IsError, repeated.Text);
            using var repeatedDocument = JsonDocument.Parse(repeated.Text);
            Assert.Equal("up-to-date", repeatedDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal(reviewBaseSha, repeatedDocument.RootElement.GetProperty("baseSha").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task GitIntegrate_UsesLocalIntegrationBranchWhenOriginDoesNotHaveIt()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            var reviewBaseSha = worktree.BaseSha;

            await GitAsync(repository, "switch", "-c", "beacon/integration");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "local-integration.txt"),
                "local integration branch contribution\n");
            await GitAsync(repository, "add", "src/app/local-integration.txt");
            await GitAsync(repository, "commit", "-m", "Create local integration contribution");
            var integrationSha = await GitAsync(repository, "rev-parse", "HEAD");
            await GitAsync(repository, "switch", "main");

            var integrated = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using var document = JsonDocument.Parse(integrated.Text);
            Assert.Equal("integrated", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(reviewBaseSha, document.RootElement.GetProperty("baseSha").GetString());
            Assert.Equal(integrationSha, document.RootElement.GetProperty("integrationSha").GetString());
            Assert.Equal(
                "local integration branch contribution\n",
                await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "local-integration.txt")));
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitIntegrate_RejectsArbitrarySourceBranchNames()
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
            var headBefore = await GitAsync(worktree.RecoveryPath, "rev-parse", "HEAD");

            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"feature/agent-selected"}""");

            Assert.True(result.IsError);
            Assert.Contains("source must be base or integration", result.Text, StringComparison.Ordinal);
            Assert.Equal(headBefore, await GitAsync(worktree.RecoveryPath, "rev-parse", "HEAD"));
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task GitIntegrate_IntegrationSourceIgnoresAnOldReviewBaseFetchRef()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            var reviewBaseSha = worktree.BaseSha;

            await GitAsync(repository, "switch", "-c", "stale-review-base", reviewBaseSha);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "stale-base-only.txt"),
                "stale review-base fetch ref\n");
            await GitAsync(repository, "add", "src/app/stale-base-only.txt");
            await GitAsync(repository, "commit", "-m", "Create a stale review-base fetch ref");
            var staleBaseSha = await GitAsync(repository, "rev-parse", "HEAD");
            await GitAsync(repository, "update-ref",
                $"refs/beacon/task/{taskId:N}/integration-base", staleBaseSha);

            await GitAsync(repository, "switch", "-c", "beacon/integration", reviewBaseSha);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "integration-only.txt"),
                "fixed integration source\n");
            await GitAsync(repository, "add", "src/app/integration-only.txt");
            await GitAsync(repository, "commit", "-m", "Create fixed integration branch");
            var integrationSha = await GitAsync(repository, "rev-parse", "HEAD");
            await GitAsync(repository, "switch", "main");

            var result = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");

            Assert.False(result.IsError, result.Text);
            using var document = JsonDocument.Parse(result.Text);
            Assert.Equal("integrated", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("integration", document.RootElement.GetProperty("source").GetString());
            Assert.Equal(reviewBaseSha, document.RootElement.GetProperty("baseSha").GetString());
            Assert.Equal(integrationSha, document.RootElement.GetProperty("integrationSha").GetString());
            Assert.Equal(
                "fixed integration source\n",
                await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "integration-only.txt")));
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitIntegrate_IntegrationConflictCanBeResolvedAndContinued()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;
            var authorization = CreateAuthorization(worktree, projectId, taskId);
            var reviewBaseSha = worktree.BaseSha;

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"),
                "task branch version\n");
            var committed = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitCommit,
                """{"message":"Change shared file on task branch"}""");
            Assert.False(committed.IsError, committed.Text);

            await GitAsync(repository, "switch", "-c", "beacon/integration");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "shared.txt"),
                "integration branch version\n");
            await GitAsync(repository, "add", "src/app/shared.txt");
            await GitAsync(repository, "commit", "-m", "Change shared file on integration branch");
            var integrationSha = await GitAsync(repository, "rev-parse", "HEAD");
            await GitAsync(repository, "push", "origin", "beacon/integration");
            await GitAsync(repository, "switch", "main");

            var integrated = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using (var document = JsonDocument.Parse(integrated.Text))
            {
                Assert.Equal("conflict", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("integration", document.RootElement.GetProperty("source").GetString());
                Assert.Equal(integrationSha, document.RootElement.GetProperty("targetSha").GetString());
                Assert.Contains(
                    "src/app/shared.txt",
                    document.RootElement.GetProperty("files").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty));
            }

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"),
                "resolved integration conflict\n");
            var continued = await ExecuteAsync(
                authorization,
                PipelineToolPolicy.GitIntegrate,
                """{"action":"continue"}""");
            Assert.False(continued.IsError, continued.Text);
            using (var document = JsonDocument.Parse(continued.Text))
            {
                Assert.Equal("integrated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("integration", document.RootElement.GetProperty("source").GetString());
                Assert.Equal(reviewBaseSha, document.RootElement.GetProperty("baseSha").GetString());
                Assert.Equal(integrationSha, document.RootElement.GetProperty("integrationSha").GetString());
            }

            Assert.Equal("resolved integration conflict\n",
                await File.ReadAllTextAsync(Path.Combine(worktree.WorkingDirectory, "shared.txt")));
            Assert.Equal(string.Empty,
                await GitAsync(worktree.RecoveryPath, "status", "--porcelain", "--untracked-files=all"));
            var info = await ExecuteAsync(
                authorization, PipelineToolPolicy.GitInfo, """{"kind":"branches"}""");
            Assert.False(info.IsError, info.Text);
            using var branch = JsonDocument.Parse(info.Text);
            Assert.Equal(reviewBaseSha, branch.RootElement.GetProperty("baseSha").GetString());
            Assert.Equal(integrationSha, branch.RootElement.GetProperty("integrationSha").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitCommit_WorksWhenProjectIsRepositoryRoot()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(
                repository, withOrigin: false, projectAtRepositoryRoot: true);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "root-project-change.txt"),
                "root project change\n");

            var committed = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitCommit,
                """{"message":"Commit repository-root project change"}""");

            Assert.False(committed.IsError, committed.Text);
            using var document = JsonDocument.Parse(committed.Text);
            Assert.True(document.RootElement.GetProperty("committed").GetBoolean());
            Assert.Contains(
                "root-project-change.txt",
                await GitAsync(worktree.RecoveryPath, "show", "--format=", "--name-only", "HEAD"),
                StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitIntegrate_RejectsTamperedBaseBranchMetadata()
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

            var metadataPath = worktree.RecoveryPath + ".beacon.json";
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
            metadata["baseBranch"] = "attacker-controlled-branch";
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());

            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge"}""");

            Assert.True(result.IsError);
            Assert.Contains("metadata does not match", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitPublish_IsDeniedWithoutExplicitServerAuthorization()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "beacon-no-publish-" + Guid.NewGuid().ToString("N"));
        var authorization = new AgentHarnessAuthorization(
            "https://beacon.invalid",
            "test-only-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            missingRoot,
            DateTime.UtcNow.AddMinutes(10),
            PipelineRole: "Actor",
            IsolatedWorkspace: true,
            GitWorkspace: new AgentGitWorkspaceAuthorization(
                missingRoot,
                missingRoot,
                "beacon/task/test",
                "main",
                new string('a', 40)));

        var result = await ExecuteAsync(
            authorization,
            PipelineToolPolicy.GitPublish,
            """{"action":"publish","title":"Publish task branch","body":"Must not push"}""");

        Assert.True(result.IsError);
        Assert.Contains("not authorized", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public void ToolDefinitions_DoNotAcceptRemoteOrBranchIdentity()
    {
        foreach (var toolName in new[]
        {
            PipelineToolPolicy.GitInfo,
            PipelineToolPolicy.GitCommit,
            PipelineToolPolicy.GitIntegrate,
            PipelineToolPolicy.GitPublish
        })
        {
            var definition = AgentTaskGitOperations.ToolDefinition(toolName);
            var properties = definition["function"]!["parameters"]!["properties"]!.AsObject();
            Assert.False(properties.ContainsKey("remote"));
            Assert.False(properties.ContainsKey("branch"));
            Assert.False(properties.ContainsKey("base"));
            Assert.False(properties.ContainsKey("target"));
        }
    }

    private static AgentHarnessAuthorization CreateAuthorization(
        AgentTaskWorktree worktree,
        Guid projectId,
        Guid taskId,
        bool allowAgentGitPublish = false) =>
        new(
            "https://beacon.invalid",
            "test-only-token",
            projectId,
            taskId,
            Guid.NewGuid(),
            1,
            worktree.WorkingDirectory,
            DateTime.UtcNow.AddMinutes(10),
            SubtaskId: Guid.NewGuid(),
            PipelineRole: "Actor",
            ProjectRoot: worktree.ProjectRoot,
            IsolatedWorkspace: true,
            GitWorkspace: new AgentGitWorkspaceAuthorization(
                worktree.RepositoryRoot,
                worktree.RecoveryPath,
                worktree.BranchName,
                worktree.BaseBranch,
                worktree.BaseSha),
            AllowAgentGitPublish: allowAgentGitPublish);

    private static async Task<McpToolResult> ExecuteAsync(
        AgentHarnessAuthorization authorization, string tool, string arguments)
    {
        var parsed = JsonNode.Parse(arguments) as JsonObject
            ?? throw new InvalidOperationException("Expected JSON object arguments.");
        return await AgentTaskGitOperations.ExecuteAsync(
            authorization, tool, parsed, CancellationToken.None);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "beacon-task-git-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateShortTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }


    private static async Task<string> InitializeRepositoryAsync(
        string repository, bool withOrigin, bool projectAtRepositoryRoot = false)
    {
        var projectRoot = projectAtRepositoryRoot
            ? repository
            : Path.Combine(repository, "src", "app");
        Directory.CreateDirectory(projectRoot);
        await GitAsync(repository, "init", "-b", "main");
        await GitAsync(repository, "config", "core.autocrlf", "false");
        await GitAsync(repository, "config", "user.name", "ProjectBeacon Tests");
        await GitAsync(repository, "config", "user.email", "tests@projectbeacon.local");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "shared.txt"), "base version\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "project readme\n");
        await GitAsync(repository, "add", "-A");
        await GitAsync(repository, "commit", "-m", "initial");

        if (withOrigin)
        {
            var origin = repository + "-origin.git";
            await GitAsync(repository, "init", "--bare", origin);
            await GitAsync(repository, "remote", "add", "origin", origin);
            await GitAsync(repository, "push", "-u", "origin", "main");
        }
        return projectRoot;
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout.TrimEnd('\r', '\n');
    }

    [Fact]
    public void GitOperationStateRead_RequiresAllThreeGitPathQueriesToSucceed()
    {
        var rebaseMerge = (0, "rebase-merge", string.Empty, false);
        var rebaseApply = (0, "rebase-apply", string.Empty, false);
        var mergeHead = (0, "MERGE_HEAD", string.Empty, false);

        Assert.Null(AgentTaskGitOperations.GetOperationStateReadError(
            rebaseMerge, rebaseApply, mergeHead));
        Assert.NotNull(AgentTaskGitOperations.GetOperationStateReadError(
            (1, string.Empty, "fatal: bad git directory", false),
            rebaseApply,
            mergeHead));
        Assert.NotNull(AgentTaskGitOperations.GetOperationStateReadError(
            rebaseMerge,
            (0, string.Empty, string.Empty, false),
            mergeHead));
        Assert.NotNull(AgentTaskGitOperations.GetOperationStateReadError(
            rebaseMerge,
            rebaseApply,
            (0, "MERGE_HEAD", string.Empty, true)));
    }


    [Fact]
    public void GitConflictListRead_ParsesAllUnmergedStagesAndPreservesPathCharacters()
    {
        var sha1 = new string('a', 40);
        var sha256 = new string('b', 64);
        var output = string.Concat(
            $"100644 {sha1} 1\tsrc/app/conflicted file.txt\0",
            $"100644 {sha1} 2\tsrc/app/conflicted file.txt\0",
            $"100644 {sha1} 3\tsrc/app/conflicted file.txt\0",
            $"100755 {sha256} 2\tsrc/app/file\twith-tab\nname.txt\0");

        var result = AgentTaskGitOperations.ParseConflictListResult(
            (0, output, string.Empty, false));

        Assert.Null(result.Error);
        Assert.Equal(
            new[] { "src/app/conflicted file.txt", "src/app/file\twith-tab\nname.txt" },
            result.Files);
    }

    [Fact]
    public void GitConflictListRead_FailsClosedForCommandFailuresAndMalformedOutput()
    {
        var sha = new string('a', 40);
        var validRecord = $"100644 {sha} 2\tconflicted.txt\0";

        var empty = AgentTaskGitOperations.ParseConflictListResult(
            (0, string.Empty, string.Empty, false));
        Assert.Null(empty.Error);
        Assert.Empty(empty.Files);

        var failed = AgentTaskGitOperations.ParseConflictListResult(
            (1, validRecord, "fatal: index unavailable", false));
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Files);

        var timedOut = AgentTaskGitOperations.ParseConflictListResult(
            (0, validRecord, string.Empty, true));
        Assert.NotNull(timedOut.Error);
        Assert.Empty(timedOut.Files);

        var malformedOutputs = new[]
        {
            validRecord[..^1],
            $"100644 {sha} 4\tconflicted.txt\0",
            $"10066z {sha} 2\tconflicted.txt\0",
            $"100644 not-a-git-object 2\tconflicted.txt\0",
            $"100644 {sha} 2\t\0",
            "not-a-git-index-record\0",
            validRecord + "\0"
        };

        foreach (var output in malformedOutputs)
        {
            var malformed = AgentTaskGitOperations.ParseConflictListResult(
                (0, output, string.Empty, false));
            Assert.NotNull(malformed.Error);
            Assert.Empty(malformed.Files);
        }
    }


    [Fact]
    public async Task GitInfo_DiffDisablesConfiguredTextconvDrivers()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, ".gitattributes"),
                "*.bin diff=beacon-textconv\n");
            await File.WriteAllBytesAsync(
                Path.Combine(projectRoot, "payload.bin"),
                [0x01, 0x02, 0x03]);
            await GitAsync(repository, "add", "-A");
            await GitAsync(repository, "commit", "-m", "Add binary textconv fixture");
            await GitAsync(repository, "config", "diff.beacon-textconv.binary", "true");
            await GitAsync(repository, "config", "diff.beacon-textconv.textconv", "git hash-object");

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            await File.WriteAllBytesAsync(
                Path.Combine(worktree.WorkingDirectory, "payload.bin"),
                [0x01, 0x02, 0x03, 0x04]);
            var currentBlob = await GitAsync(
                worktree.RecoveryPath, "hash-object", "src/app/payload.bin");

            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitInfo,
                """{"kind":"diff","path":"payload.bin"}""");

            Assert.False(result.IsError, result.Text);
            Assert.Contains("Binary files", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(currentBlob, result.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task GitIntegrate_RejectsSavedCheckpointThatDoesNotResolveToACommit()
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

            await File.WriteAllTextAsync(Path.Combine(repository, "checkpoint-blob.txt"), "not a commit\n");
            var blobSha = await GitAsync(repository, "hash-object", "-w", "checkpoint-blob.txt");
            var pendingRef = $"refs/beacon/task/{taskId:N}/integration-pending";
            await GitAsync(worktree.RecoveryPath, "update-ref", pendingRef, blobSha);

            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"merge","source":"integration"}""");

            Assert.True(result.IsError, result.Text);
            Assert.Contains("checkpoint", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("commit", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(blobSha, await GitAsync(worktree.RecoveryPath, "rev-parse", pendingRef));
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PendingRefDelete_FailsClosedForEveryUnsuccessfulGitCommand()
    {
        Assert.Null(AgentTaskGitOperations.GetPendingRefDeleteError(
            (0, string.Empty, string.Empty, false)));

        var lockFailure = AgentTaskGitOperations.GetPendingRefDeleteError(
            (1, string.Empty, "error: cannot lock ref", false));
        Assert.NotNull(lockFailure);
        Assert.Contains("cannot lock ref", lockFailure, StringComparison.Ordinal);

        var timeout = AgentTaskGitOperations.GetPendingRefDeleteError(
            (0, string.Empty, string.Empty, true));
        Assert.NotNull(timeout);
    }


    [Fact]
    public void ConfiguredMergeDriverRead_ParsesDriverNamesAndFailsClosedForInvalidResults()
    {
        var parsed = AgentTaskGitOperations.ParseConfiguredMergeDriversResult(
            (0, "merge.beacon.driver\0merge.xml.driver\0merge.beacon.driver\0", string.Empty, false));

        Assert.Null(parsed.Error);
        Assert.Equal(new[] { "beacon", "xml" }, parsed.Drivers);

        var none = AgentTaskGitOperations.ParseConfiguredMergeDriversResult(
            (1, string.Empty, string.Empty, false));
        Assert.Null(none.Error);
        Assert.Empty(none.Drivers);

        var unreadable = AgentTaskGitOperations.ParseConfiguredMergeDriversResult(
            (1, string.Empty, "fatal: unable to read config", false));
        Assert.NotNull(unreadable.Error);
        Assert.Empty(unreadable.Drivers);

        var timedOut = AgentTaskGitOperations.ParseConfiguredMergeDriversResult(
            (0, "merge.beacon.driver\0", string.Empty, true));
        Assert.NotNull(timedOut.Error);

        var malformed = new[]
        {
            "merge.beacon.driver",
            "merge..driver\0",
            "merge.beacon.driver\0unexpected.key\0",
            "merge.beacon.driver\0\0",
            "merge.bad name.driver\0"
        };
        foreach (var output in malformed)
        {
            var result = AgentTaskGitOperations.ParseConfiguredMergeDriversResult(
                (0, output, string.Empty, false));
            Assert.NotNull(result.Error);
            Assert.Empty(result.Drivers);
        }
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    public async Task GitIntegrate_DoesNotExecuteConfiguredExternalMergeDrivers(string strategy)
    {
        // Keep Windows' full path below MAX_PATH while Git rebase inspects its
        // internal A...B paths from this nested task worktree.
        var root = CreateShortTempRoot();
        var repository = Path.Combine(root, "r");
        var worktreeRoot = Path.Combine(root, "w");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, ".gitattributes"),
                "*.txt merge=beacon\n");
            await GitAsync(repository, "add", "src/app/.gitattributes");
            await GitAsync(repository, "commit", "-m", "Configure custom merge-driver attribute");
            await GitAsync(repository, "push", "origin", "main");

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            await GitAsync(repository, "config", "merge.beacon.name", "External test merge driver");
            await GitAsync(repository, "config", "merge.beacon.driver",
                "git update-ref refs/beacon/tests/merge-driver-executed HEAD");

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "task branch version\n");
            var taskCommit = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitCommit,
                """{"message":"Change shared file in task branch"}""");
            Assert.False(taskCommit.IsError, taskCommit.Text);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "shared.txt"), "upstream version\n");
            await GitAsync(repository, "add", "src/app/shared.txt");
            await GitAsync(repository, "commit", "-m", "Change shared file on base");
            await GitAsync(repository, "push", "origin", "main");

            var integrated = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                $$"""{"action":"integrate","strategy":"{{strategy}}","source":"base"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using (var document = JsonDocument.Parse(integrated.Text))
            {
                Assert.Equal("conflict", document.RootElement.GetProperty("status").GetString());
                Assert.Contains(
                    "src/app/shared.txt",
                    document.RootElement.GetProperty("files").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty));
            }

            var markerRefs = await GitAsync(
                worktree.RecoveryPath,
                "for-each-ref",
                "--format=%(refname)",
                "refs/beacon/tests/merge-driver-executed");
            Assert.Equal(string.Empty, markerRefs);
            Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt")), StringComparison.Ordinal);

            var aborted = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"abort"}""");
            Assert.False(aborted.IsError, aborted.Text);
            using var abortDocument = JsonDocument.Parse(aborted.Text);
            Assert.Equal("aborted", abortDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Theory]
    [InlineData("merge")]
    [InlineData("apply")]
    public async Task GitIntegrate_RebaseContinueAlsoNeutralizesCustomMergeDrivers(string rebaseBackend)
    {
        // Keep Windows' full path below MAX_PATH while Git rebase inspects its
        // internal A...B paths from this nested task worktree.
        var root = CreateShortTempRoot();
        var repository = Path.Combine(root, "r");
        var worktreeRoot = Path.Combine(root, "w");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: true);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "shared.txt"), "base-alpha\nbase-beta\n");
            await GitAsync(repository, "add", "src/app/shared.txt");
            await GitAsync(repository, "commit", "-m", "Prepare two-commit rebase conflict");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, ".gitattributes"), "*.txt merge=beacon\n");
            await GitAsync(repository, "add", "src/app/.gitattributes");
            await GitAsync(repository, "commit", "-m", "Configure custom merge-driver attribute");
            await GitAsync(repository, "push", "origin", "main");
            await GitAsync(repository, "config", "rebase.backend", rebaseBackend);

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            await GitAsync(repository, "config", "merge.beacon.name", "External test merge driver");
            await GitAsync(repository, "config", "merge.beacon.driver",
                "git update-ref refs/beacon/tests/merge-driver-executed HEAD");

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "task-alpha\nbase-beta\n");
            var firstCommit = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitCommit,
                """{"message":"Change first line in task branch"}""");
            Assert.False(firstCommit.IsError, firstCommit.Text);

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "task-alpha\ntask-beta\n");
            var secondCommit = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitCommit,
                """{"message":"Change second line in task branch"}""");
            Assert.False(secondCommit.IsError, secondCommit.Text);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "shared.txt"), "upstream-alpha\nupstream-beta\n");
            await GitAsync(repository, "add", "src/app/shared.txt");
            await GitAsync(repository, "commit", "-m", "Change both lines on base");
            await GitAsync(repository, "push", "origin", "main");

            var integrated = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"integrate","strategy":"rebase","source":"base"}""");
            Assert.False(integrated.IsError, integrated.Text);
            using (var document = JsonDocument.Parse(integrated.Text))
                Assert.Equal("conflict", document.RootElement.GetProperty("status").GetString());

            await File.WriteAllTextAsync(
                Path.Combine(worktree.WorkingDirectory, "shared.txt"), "resolved-alpha\nupstream-beta\n");

            var commitDuringRebase = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitCommit,
                """{"message":"Must not commit during rebase"}""");
            Assert.True(commitDuringRebase.IsError, commitDuringRebase.Text);
            Assert.Contains("not checked out on its assigned task branch",
                commitDuringRebase.Text, StringComparison.OrdinalIgnoreCase);

            var rebaseStateDirectory = rebaseBackend == "merge" ? "rebase-merge" : "rebase-apply";
            var rebaseHeadNamePath = await GitAsync(
                worktree.RecoveryPath, "rev-parse", "--git-path", Path.Combine(rebaseStateDirectory, "head-name"));
            rebaseHeadNamePath = Path.GetFullPath(rebaseHeadNamePath, worktree.RecoveryPath);
            var originalHeadName = await File.ReadAllTextAsync(rebaseHeadNamePath);
            await File.WriteAllTextAsync(rebaseHeadNamePath, "refs/heads/beacon/task/not-this-task\n");

            var mismatchedRebase = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"continue"}""");
            Assert.True(mismatchedRebase.IsError, mismatchedRebase.Text);

            await File.WriteAllTextAsync(rebaseHeadNamePath, originalHeadName);

            var rebaseDirectory = Path.GetDirectoryName(rebaseHeadNamePath)!;
            var rebaseOriginalHeadPath = Path.Combine(rebaseDirectory, "orig-head");
            var originalRebaseHead = await File.ReadAllTextAsync(rebaseOriginalHeadPath);
            await File.WriteAllTextAsync(rebaseOriginalHeadPath, new string('0', 40) + "\n");
            var mismatchedOriginalHead = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"continue"}""");
            Assert.True(mismatchedOriginalHead.IsError, mismatchedOriginalHead.Text);
            await File.WriteAllTextAsync(rebaseOriginalHeadPath, originalRebaseHead);

            var continued = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"continue"}""");
            Assert.False(continued.IsError, continued.Text);
            using (var document = JsonDocument.Parse(continued.Text))
            {
                Assert.Equal("conflict", document.RootElement.GetProperty("status").GetString());
                Assert.Contains(
                    "src/app/shared.txt",
                    document.RootElement.GetProperty("files").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty));
            }

            var markerRefs = await GitAsync(
                worktree.RecoveryPath,
                "for-each-ref",
                "--format=%(refname)",
                "refs/beacon/tests/merge-driver-executed");
            Assert.Equal(string.Empty, markerRefs);

            var aborted = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitIntegrate,
                """{"action":"abort"}""");
            Assert.False(aborted.IsError, aborted.Text);
            using var abortDocument = JsonDocument.Parse(aborted.Text);
            Assert.Equal("aborted", abortDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Fact]
    public async Task GitInfo_RejectsDetachedTaskHeadOutsideAnActiveTaskRebase()
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

            await GitAsync(worktree.RecoveryPath, "checkout", "--detach", "HEAD");
            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitInfo,
                """{"kind":"status"}""");

            Assert.True(result.IsError, result.Text);
            Assert.Contains("not checked out on its assigned task branch", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (worktree is not null)
            {
                try { await GitAsync(worktree.RecoveryPath, "checkout", worktree.BranchName); } catch { }
                await worktree.DisposeAsync();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GitInfo_BlameDisablesConfiguredTextconvDrivers()
    {
        var root = CreateTempRoot();
        var repository = Path.Combine(root, "repository");
        var worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? worktree = null;
        try
        {
            var projectRoot = await InitializeRepositoryAsync(repository, withOrigin: false);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, ".gitattributes"), "*.txt diff=beacon-textconv\n");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "blame.txt"), "alpha\nbeta\ngamma\n");
            await GitAsync(repository, "add", "-A");
            await GitAsync(repository, "commit", "-m", "Configure textconv blame fixture");
            await GitAsync(repository, "config", "diff.beacon-textconv.textconv", "git hash-object");

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var opened = await AgentTaskWorktree.CreateAsync(
                projectRoot, projectId, taskId, worktreeRoot, CancellationToken.None);
            Assert.NotNull(opened.Worktree);
            Assert.Null(opened.Error);
            worktree = opened.Worktree!;

            var blobSha = await GitAsync(
                worktree.RecoveryPath, "hash-object", "src/app/blame.txt");
            var result = await ExecuteAsync(
                CreateAuthorization(worktree, projectId, taskId),
                PipelineToolPolicy.GitInfo,
                """{"kind":"blame","path":"blame.txt"}""");

            Assert.False(result.IsError, result.Text);
            Assert.Contains("alpha", result.Text, StringComparison.Ordinal);
            Assert.Contains("beta", result.Text, StringComparison.Ordinal);
            Assert.Contains("gamma", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(blobSha, result.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (worktree is not null)
                await worktree.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

}
