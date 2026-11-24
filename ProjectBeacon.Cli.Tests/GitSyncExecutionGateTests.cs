namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class GitSyncExecutionGateTests
{
    [Fact]
    public async Task AutomaticTryAcquireSkipsWhenManualGitSyncOwnsGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-git-sync-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var manual = await GitSyncExecutionGate.AcquireAsync(root, CancellationToken.None);
            var automatic = await GitSyncExecutionGate.TryAcquireAsync(root, CancellationToken.None);
            Assert.Null(automatic);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GitSyncGateIsIndependentFromAgentSessionRepositoryGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-git-sync-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var session = GitRepositoryOperationGate.RegisterSession(root);
            using var sync = await GitSyncExecutionGate.TryAcquireAsync(root, CancellationToken.None);
            Assert.NotNull(sync);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
