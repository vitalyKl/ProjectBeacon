namespace ProjectBeacon.Web.Tests;

using ProjectBeacon.Web.Shared;

public sealed class UiBackgroundFailureTrackerTests
{
    [Fact]
    public void RecordFailure_ReportsOnlyAfterThreeConsecutiveFailures()
    {
        var tracker = new UiBackgroundFailureTracker();

        Assert.False(tracker.RecordFailure());
        Assert.False(tracker.RecordFailure());
        Assert.True(tracker.RecordFailure());
        Assert.False(tracker.RecordFailure());
        Assert.False(tracker.RecordFailure());
    }

    [Fact]
    public void RecordSuccess_ResetsFailuresAndSignalsRecoveryOnce()
    {
        var tracker = new UiBackgroundFailureTracker();

        Assert.False(tracker.RecordFailure());
        Assert.False(tracker.RecordFailure());
        Assert.True(tracker.RecordFailure());
        Assert.True(tracker.RecordSuccess());
        Assert.False(tracker.RecordSuccess());
        Assert.False(tracker.RecordFailure());
        Assert.False(tracker.RecordFailure());
        Assert.True(tracker.RecordFailure());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiBackgroundFailureTracker(0));
    }
}
