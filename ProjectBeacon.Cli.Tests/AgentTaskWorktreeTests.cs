namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using ProjectBeacon.Cli.Client;

public sealed class AgentTaskWorktreeTests
{
    [Fact]
    public async Task CreateAndReuse_UsesPersistentTaskBranchFromCommittedBase()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-worktree-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            var baseSha = await GitAsync(repository, "rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "local uncommitted edit\n");

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var created = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);

            Assert.NotNull(created.Worktree);
            Assert.Null(created.Error);
            Assert.Contains("origin", created.Warning ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            var workspace = created.Worktree!;
            Assert.Equal($"beacon/task/{taskId:N}", workspace.BranchName);
            Assert.Contains(Path.Combine("worktrees", projectId.ToString("N")),
                workspace.RecoveryPath, StringComparison.Ordinal);
            Assert.Equal("committed base\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkingDirectory, "README.md")));
            Assert.Equal(baseSha, await GitAsync(workspace.RecoveryPath, "rev-parse", "HEAD"));
            Assert.Equal("local uncommitted edit\n", await File.ReadAllTextAsync(Path.Combine(repository, "README.md")));

            await File.WriteAllTextAsync(Path.Combine(workspace.WorkingDirectory, "agent-output.txt"), "completed actor work\n");
            var completed = await workspace.CompleteChangesAsync("ProjectBeacon: complete task actor", CancellationToken.None);
            Assert.True(completed.Success, completed.Error);
            Assert.Matches("^[0-9a-f]{40}$", completed.CommitSha ?? string.Empty);
            Assert.Equal(completed.CommitSha, await GitAsync(workspace.RecoveryPath, "rev-parse", "HEAD"));
            Assert.Equal(baseSha, await GitAsync(repository, "rev-parse", "HEAD"));
            await workspace.DisposeAsync();

            var reopened = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.True(reopened.Worktree is not null, reopened.Error);
            Assert.Null(reopened.Error);
            await using var reopenedWorkspace = reopened.Worktree!;
            Assert.Equal(workspace.RecoveryPath, reopenedWorkspace.RecoveryPath);
            Assert.Equal(workspace.BranchName, reopenedWorkspace.BranchName);
            Assert.Equal("completed actor work\n",
                await File.ReadAllTextAsync(Path.Combine(reopenedWorkspace.WorkingDirectory, "agent-output.txt")));
            Assert.Equal(completed.CommitSha,
                await GitAsync(reopenedWorkspace.RecoveryPath, "rev-parse", "HEAD"));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Dispose_CommitsOutstandingChangesAsWorkInProgress()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-worktree-wip-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            var created = await AgentTaskWorktree.CreateAsync(
                repository, Guid.NewGuid(), Guid.NewGuid(), worktrees, CancellationToken.None);
            Assert.NotNull(created.Worktree);
            Assert.Null(created.Error);

            var workspace = created.Worktree!;
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkingDirectory, "unfinished.txt"), "keep this work\n");
            await workspace.DisposeAsync();

            Assert.Equal(
                "ProjectBeacon: WIP task " + workspace.RecoveryPath.Split(Path.DirectorySeparatorChar).Last(),
                await GitAsync(workspace.RecoveryPath, "log", "-1", "--format=%s"));
            Assert.Equal(string.Empty,
                await GitAsync(workspace.RecoveryPath, "status", "--porcelain", "--untracked-files=all"));
            Assert.Equal("keep this work\n",
                await File.ReadAllTextAsync(Path.Combine(workspace.RecoveryPath, "unfinished.txt")));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Create_RejectsWorktreeRootInsideTheSourceRepository()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-worktree-root-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            var created = await AgentTaskWorktree.CreateAsync(
                repository, Guid.NewGuid(), Guid.NewGuid(),
                Path.Combine(repository, ".beacon", "worktrees"),
                CancellationToken.None);

            Assert.Null(created.Worktree);
            Assert.Contains("outside", created.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Create_RejectsConcurrentUseOfTheSameTaskWorktree()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-worktree-lock-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var first = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.NotNull(first.Worktree);
            await using var active = first.Worktree!;

            var second = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.Null(second.Worktree);
            Assert.Contains("already in use", second.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("git version 2.37.9", false)]
    [InlineData("git version 2.38.0", true)]
    [InlineData("git version 2.45.1.windows.1", true)]
    [InlineData("git version 3.0.0", true)]
    [InlineData("not a git version", false)]
    public void SupportsBranchWorkflow_RequiresGit238OrNewer(string version, bool expected)
    {
        Assert.Equal(expected, AgentTaskWorktree.SupportsBranchWorkflow(version));
    }

    [Fact]
    public void WorkstationSettings_DefaultToSnapshotMode()
    {
        Assert.Equal(WorkstationWorkspaceMode.Snapshot, new WorkstationSettings().WorkspaceMode);
    }

    private static async Task InitializeRepositoryAsync(string repository)
    {
        await GitAsync(repository, "init", "-b", "main");
        await GitAsync(repository, "config", "core.autocrlf", "false");
        await GitAsync(repository, "config", "user.name", "ProjectBeacon Tests");
        await GitAsync(repository, "config", "user.email", "tests@projectbeacon.local");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "committed base\n");
        await GitAsync(repository, "add", "README.md");
        await GitAsync(repository, "commit", "-m", "initial");
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
