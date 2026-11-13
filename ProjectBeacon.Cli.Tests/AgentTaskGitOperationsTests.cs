namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class AgentTaskGitOperationsTests
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
                await worktree.DisposeAsync();
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
    public void ToolDefinitions_DoNotAcceptRemoteOrBranchIdentity()
    {
        foreach (var toolName in new[]
        {
            PipelineToolPolicy.GitInfo,
            PipelineToolPolicy.GitCommit,
            PipelineToolPolicy.GitIntegrate
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
        AgentTaskWorktree worktree, Guid projectId, Guid taskId) =>
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
                worktree.BaseSha));

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

    private static async Task<string> InitializeRepositoryAsync(string repository, bool withOrigin)
    {
        var projectRoot = Path.Combine(repository, "src", "app");
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
}
