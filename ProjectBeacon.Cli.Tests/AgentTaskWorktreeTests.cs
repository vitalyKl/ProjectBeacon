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
            // A Branch-mode task workspace is isolated by its own branch and lease; it must
            // not be counted as a live Snapshot session that blocks repository GitSync.
            Assert.False(GitRepositoryOperationGate.HasActiveSessions(repository));
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
    public async Task ReopenedTaskWorkspace_ReportsBaseBranchLagWithoutIntegratingAutomatically()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-base-lag-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            await GitAsync(parent, "init", "--bare", "--initial-branch=main", remote);
            await GitAsync(repository, "remote", "add", "origin", remote);
            await GitAsync(repository, "push", "-u", "origin", "main");
            await GitAsync(parent, "clone", remote, seed);
            await GitAsync(seed, "config", "user.name", "ProjectBeacon Tests");
            await GitAsync(seed, "config", "user.email", "tests@projectbeacon.local");

            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var first = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.NotNull(first.Worktree);
            await first.Worktree!.DisposeAsync();

            await File.WriteAllTextAsync(Path.Combine(seed, "remote-update.txt"), "new base commit\n");
            await GitAsync(seed, "add", "remote-update.txt");
            await GitAsync(seed, "commit", "-m", "advance base branch");
            await GitAsync(seed, "push", "origin", "main");
            var originalTaskHead = await GitAsync(repository, "rev-parse", $"refs/heads/beacon/task/{taskId:N}");

            var reopened = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.NotNull(reopened.Worktree);
            await using var workspace = reopened.Worktree!;
            Assert.Equal(1, workspace.BaseBehindCommits);
            Assert.Contains("advanced by 1 commits", workspace.BuildEnvironmentContract(), StringComparison.Ordinal);
            Assert.Contains("git_integrate", workspace.BuildEnvironmentContract(), StringComparison.Ordinal);
            Assert.Equal(originalTaskHead,
                await GitAsync(repository, "rev-parse", $"refs/heads/beacon/task/{taskId:N}"));
            Assert.False(File.Exists(Path.Combine(workspace.WorkingDirectory, "remote-update.txt")));
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
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("ssh://git@github.com/owner/repo.git", true)]
    [InlineData("git@github.com:owner/repo.git", true)]
    [InlineData("/tmp/remote.git", true)]
    [InlineData("../remote.git", true)]
    [InlineData("ext::sh -c malicious", false)]
    [InlineData("custom-helper://host/repo", false)]
    [InlineData("rsync://host/repo", false)]
    public void IsSupportedFetchRemote_RejectsCustomRemoteHelpers(string remote, bool expected)
    {
        Assert.Equal(expected, AgentTaskWorktree.IsSupportedFetchRemote(remote));
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
    public void WorkstationSettings_DefaultToPersistentTaskBranches()
    {
        var settings = new WorkstationSettings();
        Assert.Equal(WorkstationWorkspaceMode.Branch, settings.WorkspaceMode);
        Assert.False(settings.WorkspaceModeConfigured);
        Assert.Equal(30, settings.TaskWorktreeRetentionDays);
    }

    [Fact]
    public async Task WorkstationSettings_MigratesLegacyImplicitSnapshotButPreservesExplicitChoice()
    {
        var path = Path.Combine(Path.GetTempPath(), "beacon-workstation-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"workspaceMode\":\"Snapshot\"}");
            var (legacy, legacyError) = WorkstationSettings.TryRead(path);
            Assert.Null(legacyError);
            Assert.Equal(WorkstationWorkspaceMode.Branch, legacy.WorkspaceMode);
            Assert.False(legacy.WorkspaceModeConfigured);

            await File.WriteAllTextAsync(path, "{\"workspaceMode\":\"Snapshot\",\"workspaceModeConfigured\":true}");
            var (explicitSnapshot, explicitError) = WorkstationSettings.TryRead(path);
            Assert.Null(explicitError);
            Assert.Equal(WorkstationWorkspaceMode.Snapshot, explicitSnapshot.WorkspaceMode);
            Assert.True(explicitSnapshot.WorkspaceModeConfigured);

            explicitSnapshot.Save(path);
            var (roundTripped, roundTripError) = WorkstationSettings.TryRead(path);
            Assert.Null(roundTripError);
            Assert.Equal(WorkstationWorkspaceMode.Snapshot, roundTripped.WorkspaceMode);
            Assert.True(roundTripped.WorkspaceModeConfigured);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task TaskWorktreeLifecycle_PrunesExpiredCleanTaskBranchOnlyAfterVerifiedLanding()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-retention-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(repository);
        try
        {
            await InitializeRepositoryAsync(repository);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var created = await AgentTaskWorktree.CreateAsync(
                repository, projectId, taskId, worktrees, CancellationToken.None);
            Assert.NotNull(created.Worktree);
            await using var workspace = created.Worktree!;

            await File.WriteAllTextAsync(
                Path.Combine(workspace.WorkingDirectory, "task-output.txt"), "merged task output\n");
            var source = await workspace.CompleteChangesAsync(
                "ProjectBeacon: complete task", CancellationToken.None);
            Assert.True(source.Success, source.Error);

            await GitAsync(repository, "merge", "--no-ff", workspace.BranchName, "-m", "land task branch");
            var mergeCommit = await GitAsync(repository, "rev-parse", "HEAD");
            var marked = await workspace.MarkLandedAsync(
                "main", source.CommitSha!, mergeCommit, DateTime.UtcNow.AddDays(-31), CancellationToken.None);
            Assert.True(marked.Success, marked.Error);
            await workspace.DisposeAsync();

            var entries = await TaskWorktreeLifecycle.ListAsync(worktrees, 30, CancellationToken.None);
            var entry = Assert.Single(entries);
            Assert.Equal("expired-merged", entry.Status);
            Assert.True(entry.CleanupEligible);
            Assert.Equal(source.CommitSha, await GitAsync(repository, "rev-parse", $"refs/heads/{workspace.BranchName}"));

            var cleanup = await TaskWorktreeLifecycle.CleanupExpiredAsync(worktrees, 30, CancellationToken.None);
            Assert.Equal(1, cleanup.Removed);
            Assert.Equal(0, cleanup.Skipped);
            Assert.False(Directory.Exists(workspace.RecoveryPath));
            Assert.False(File.Exists(workspace.RecoveryPath + ".beacon.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => GitAsync(repository, "rev-parse", "--verify", $"refs/heads/{workspace.BranchName}"));
            Assert.Equal("merged task output\n",
                await File.ReadAllTextAsync(Path.Combine(repository, "task-output.txt")));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TaskWorktreeLifecycle_RetainsActiveDirtyAndAdvancedBranches()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-task-retention-guards-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var worktrees = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(repository);
        AgentTaskWorktree? active = null;
        AgentTaskWorktree? dirty = null;
        AgentTaskWorktree? advanced = null;

        async Task<AgentTaskWorktree> CreateLandedTaskAsync(Guid taskId, string fileName)
        {
            var created = await AgentTaskWorktree.CreateAsync(
                repository, Guid.NewGuid(), taskId, worktrees, CancellationToken.None);
            Assert.NotNull(created.Worktree);
            var workspace = created.Worktree!;
            await File.WriteAllTextAsync(
                Path.Combine(workspace.WorkingDirectory, fileName), "task output\n");
            var source = await workspace.CompleteChangesAsync(
                "ProjectBeacon: complete " + taskId.ToString("N"), CancellationToken.None);
            Assert.True(source.Success, source.Error);
            await GitAsync(repository, "merge", "--no-ff", workspace.BranchName, "-m", "land " + taskId.ToString("N"));
            var mergeCommit = await GitAsync(repository, "rev-parse", "HEAD");
            var marked = await workspace.MarkLandedAsync(
                "main", source.CommitSha!, mergeCommit, DateTime.UtcNow.AddDays(-31), CancellationToken.None);
            Assert.True(marked.Success, marked.Error);
            return workspace;
        }

        try
        {
            await InitializeRepositoryAsync(repository);

            active = await CreateLandedTaskAsync(Guid.NewGuid(), "active-output.txt");

            dirty = await CreateLandedTaskAsync(Guid.NewGuid(), "dirty-output.txt");
            await dirty.DisposeAsync();
            await File.WriteAllTextAsync(Path.Combine(dirty.RecoveryPath, "uncommitted-after-land.txt"), "keep me\n");

            advanced = await CreateLandedTaskAsync(Guid.NewGuid(), "advanced-output.txt");
            await File.WriteAllTextAsync(
                Path.Combine(advanced.WorkingDirectory, "new-work-after-land.txt"), "new task work\n");
            var laterCommit = await advanced.CompleteChangesAsync(
                "ProjectBeacon: new work after landing", CancellationToken.None);
            Assert.True(laterCommit.Success, laterCommit.Error);
            await advanced.DisposeAsync();

            var entries = await TaskWorktreeLifecycle.ListAsync(worktrees, 30, CancellationToken.None);
            Assert.Contains(entries, entry => entry.TaskId == Guid.ParseExact(active.BranchName["beacon/task/".Length..], "N")
                && entry.Status == "active");
            Assert.Contains(entries, entry => entry.TaskId == Guid.ParseExact(dirty.BranchName["beacon/task/".Length..], "N")
                && entry.Status == "dirty");
            Assert.Contains(entries, entry => entry.TaskId == Guid.ParseExact(advanced.BranchName["beacon/task/".Length..], "N")
                && entry.Status == "changed-after-landing");
            Assert.DoesNotContain(entries, entry => entry.CleanupEligible);

            var cleanup = await TaskWorktreeLifecycle.CleanupExpiredAsync(worktrees, 30, CancellationToken.None);
            Assert.Equal(0, cleanup.Removed);
            Assert.True(Directory.Exists(active.RecoveryPath));
            Assert.True(Directory.Exists(dirty.RecoveryPath));
            Assert.True(Directory.Exists(advanced.RecoveryPath));
        }
        finally
        {
            if (active is not null)
                await active.DisposeAsync();
            if (dirty is not null)
                await dirty.DisposeAsync();
            if (advanced is not null)
                await advanced.DisposeAsync();
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
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
