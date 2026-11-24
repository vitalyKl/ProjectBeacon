namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class GitAutoSyncSnapshotTests
{
    [Fact]
    public void Set_RefreshesHeartbeatTimeWithoutLoggingUnchangedState()
    {
        var snapshot = new GitAutoSyncSnapshot();
        var projectId = Guid.NewGuid();
        var firstAt = DateTimeOffset.UtcNow;
        var first = new GitAutoSyncProjectState(
            projectId, "main", "origin/main", 0, 0, true, false, true, false,
            firstAt, null, null, null);
        var refreshed = first with { CheckedAt = firstAt.AddMinutes(10) };

        Assert.True(snapshot.Set(first));
        Assert.False(snapshot.Set(refreshed));
        Assert.Equal(refreshed.CheckedAt, snapshot.Get(projectId)!.CheckedAt);
    }

    [Fact]
    public void Set_LogsNewErrorCodeEvenWhenErrorTimestampChanges()
    {
        var snapshot = new GitAutoSyncSnapshot();
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var state = new GitAutoSyncProjectState(
            projectId, "main", "origin/main", 0, 1, true, false, true, false,
            now, null, new GitAutoSyncError("FetchFailed", now), "FetchFailed");
        var repeated = state with
        {
            CheckedAt = now.AddMinutes(1),
            LastError = new GitAutoSyncError("FetchFailed", now.AddMinutes(1))
        };
        var changed = repeated with
        {
            LastError = new GitAutoSyncError("AuthenticationFailed", now.AddMinutes(2)),
            AutoPullBlockedReason = "AuthenticationFailed"
        };

        Assert.True(snapshot.Set(state));
        Assert.False(snapshot.Set(repeated));
        Assert.True(snapshot.Set(changed));
    }
}
