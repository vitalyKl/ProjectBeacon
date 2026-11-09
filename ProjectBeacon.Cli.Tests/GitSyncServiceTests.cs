namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Text.Json;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Cli.Client;

public sealed class GitSyncServiceTests
{
    private readonly GitSyncService _service = new();

    [Fact]
    public async Task PullFastForward_UpdatesOnlyTrackedBranch_AndRepeatedDeliveryIsIdempotent()
    {
        await using var repo = await RepositoryFixture.CreateAsync();
        await repo.PushRemoteCommitAsync("remote.txt", "upstream change");

        var before = await Git(repo.Root, "rev-parse", "HEAD");
        var result = await ExecuteAsync(repo, GitSyncMode.PullFastForward);
        var after = await Git(repo.Root, "rev-parse", "HEAD");

        Assert.Equal(GitSyncOutcome.FastForwarded, result.Outcome);
        Assert.NotEqual(before, after);
        Assert.Equal(before, result.HeadBefore);
        Assert.Equal(after, result.HeadAfter);
        Assert.Equal(0, result.Behind);
        Assert.True(result.Clean);
        Assert.Equal("upstream change", await File.ReadAllTextAsync(Path.Combine(repo.Root, "remote.txt")));

        var repeated = await ExecuteAsync(repo, GitSyncMode.PullFastForward);
        Assert.Equal(GitSyncOutcome.UpToDate, repeated.Outcome);
        Assert.Equal(after, repeated.HeadAfter);
    }

    [Fact]
    public async Task PullFastForward_RefusesDirtyWorkingTreeWithoutChangingHeadOrFile()
    {
        await using var repo = await RepositoryFixture.CreateAsync();
        await repo.PushRemoteCommitAsync("remote.txt", "upstream change");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "README.md"), "local uncommitted edit");
        var head = await Git(repo.Root, "rev-parse", "HEAD");

        var result = await ExecuteAsync(repo, GitSyncMode.PullFastForward);

        Assert.Equal(GitSyncOutcome.Refused, result.Outcome);
        Assert.Equal(GitSyncRefusalCode.DirtyWorkingTree, result.ReasonCode);
        Assert.Equal(head, await Git(repo.Root, "rev-parse", "HEAD"));
        Assert.Equal("local uncommitted edit", await File.ReadAllTextAsync(Path.Combine(repo.Root, "README.md")));
        Assert.False(result.Clean);
    }

    [Fact]
    public async Task PullFastForward_RefusesDivergedDetachedAndMissingUpstreamStates()
    {
        await using (var diverged = await RepositoryFixture.CreateAsync())
        {
            await File.WriteAllTextAsync(Path.Combine(diverged.Root, "local.txt"), "local");
            await Git(diverged.Root, "add", "local.txt");
            await Git(diverged.Root, "commit", "-m", "local commit");
            await diverged.PushRemoteCommitAsync("remote.txt", "remote");
            var result = await ExecuteAsync(diverged, GitSyncMode.PullFastForward);
            Assert.Equal(GitSyncOutcome.Refused, result.Outcome);
            Assert.Equal(GitSyncRefusalCode.Diverged, result.ReasonCode);
            Assert.True(result.Diverged);
        }

        await using (var detached = await RepositoryFixture.CreateAsync())
        {
            await Git(detached.Root, "checkout", "--detach", "HEAD");
            var status = await ExecuteAsync(detached, GitSyncMode.Status);
            Assert.Equal(GitSyncOutcome.UpToDate, status.Outcome);
            Assert.True(status.Detached);

            var pull = await ExecuteAsync(detached, GitSyncMode.PullFastForward);
            Assert.Equal(GitSyncOutcome.Refused, pull.Outcome);
            Assert.Equal(GitSyncRefusalCode.Detached, pull.ReasonCode);
        }

        await using (var noUpstream = await RepositoryFixture.CreateAsync())
        {
            await Git(noUpstream.Root, "branch", "--unset-upstream");
            var status = await ExecuteAsync(noUpstream, GitSyncMode.Status);
            Assert.Equal(GitSyncOutcome.UpToDate, status.Outcome);
            Assert.False(status.HasUpstream);

            var pull = await ExecuteAsync(noUpstream, GitSyncMode.PullFastForward);
            Assert.Equal(GitSyncOutcome.Refused, pull.Outcome);
            Assert.Equal(GitSyncRefusalCode.NoUpstream, pull.ReasonCode);
        }
    }

    [Fact]
    public async Task PullFastForward_RefusesWhileAgentSnapshotIsActive()
    {
        await using var repo = await RepositoryFixture.CreateAsync();
        await repo.PushRemoteCommitAsync("remote.txt", "upstream change");

        var created = await AgentSessionWorktree.CreateAsync(repo.Root, Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(created.Workspace);
        Assert.Null(created.Error);
        await using var workspace = created.Workspace!;

        var result = await ExecuteAsync(repo, GitSyncMode.PullFastForward);

        Assert.Equal(GitSyncOutcome.Refused, result.Outcome);
        Assert.Equal(GitSyncRefusalCode.ActiveSessions, result.ReasonCode);
        Assert.False(File.Exists(Path.Combine(repo.Root, "remote.txt")));
    }

    [Fact]
    public async Task PullFastForward_DisablesPostMergeHooks()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var repo = await RepositoryFixture.CreateAsync();
        await repo.PushRemoteCommitAsync("remote.txt", "upstream change");

        var hooks = Path.Combine(repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var marker = Path.Combine(repo.Parent, "post-merge-ran");
        var hook = Path.Combine(hooks, "post-merge");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf ran > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await ExecuteAsync(repo, GitSyncMode.PullFastForward);

        Assert.Equal(GitSyncOutcome.FastForwarded, result.Outcome);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Execute_RejectsPayloadWithAnyPathOrExtraArgument()
    {
        await using var repo = await RepositoryFixture.CreateAsync();
        var payload = JsonSerializer.Serialize(new
        {
            projectId = repo.ProjectId,
            mode = "Status",
            path = "C:/outside"
        });

        var result = await _service.ExecuteAsync(repo.Root, repo.ProjectId, payload, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("invalid", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitSyncResultDto> ExecuteAsync(RepositoryFixture repo, GitSyncMode mode)
    {
        var payload = JsonSerializer.Serialize(new { projectId = repo.ProjectId, mode = mode.ToString() });
        var result = await _service.ExecuteAsync(repo.Root, repo.ProjectId, payload, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Result);
        return JsonSerializer.Deserialize<GitSyncResultDto>(
            result.Result!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private RepositoryFixture(string parent, string root, string remote, string seed)
        {
            Parent = parent;
            Root = root;
            Remote = remote;
            Seed = seed;
            ProjectId = Guid.NewGuid();
        }

        public string Parent { get; }
        public string Root { get; }
        public string Remote { get; }
        public string Seed { get; }
        public Guid ProjectId { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var parent = Path.Combine(Path.GetTempPath(), "beacon-git-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);
            var root = Path.Combine(parent, "project");
            var remote = Path.Combine(parent, "origin.git");
            var seed = Path.Combine(parent, "seed");
            var fixture = new RepositoryFixture(parent, root, remote, seed);
            try
            {
                await Git(parent, "init", "--bare", "--initial-branch=main", remote);
                await Git(parent, "init", "-b", "main", root);
                await ConfigureIdentityAsync(root);
                await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
                await Git(root, "add", "README.md");
                await Git(root, "commit", "-m", "initial");
                await Git(root, "remote", "add", "origin", remote);
                await Git(root, "push", "-u", "origin", "main");

                await Git(parent, "clone", remote, seed);
                await ConfigureIdentityAsync(seed);
                return fixture;
            }
            catch
            {
                try { Directory.Delete(parent, recursive: true); } catch { }
                throw;
            }
        }

        public async Task PushRemoteCommitAsync(string fileName, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(Seed, fileName), content);
            await Git(Seed, "add", fileName);
            await Git(Seed, "commit", "-m", "remote update");
            await Git(Seed, "push", "origin", "main");
        }

        public async ValueTask DisposeAsync()
        {
            try { Directory.Delete(Parent, recursive: true); } catch { }
            await ValueTask.CompletedTask;
        }

        private static async Task ConfigureIdentityAsync(string directory)
        {
            await Git(directory, "config", "user.name", "ProjectBeacon Tests");
            await Git(directory, "config", "user.email", "tests@projectbeacon.local");
            await Git(directory, "config", "commit.gpgsign", "false");
        }
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Git in the GitSync test.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} exceeded 30 seconds.");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
