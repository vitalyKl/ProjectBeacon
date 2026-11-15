namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Tasks;
using ProjectBeacon.Domain.Enums;

public sealed class LandingAttemptTests
{
    private static LandingAttempt Start() =>
        LandingAttempt.Start(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "main",
            new string('a', 40),
            new string('c', 40),
            new string('b', 64),
            "Verified task branch change.");

    [Fact]
    public void PreviewedAttempt_CanBeExecutedAndLanded()
    {
        var attempt = Start();
        var targetHead = new string('c', 40);
        var tree = new string('d', 40);
        var outputHash = new string('e', 64);
        attempt.RecordPreview("main", targetHead, tree, outputHash, 0, null);

        Assert.Equal(LandingAttemptStatus.Previewed, attempt.Status);
        attempt.MarkExecutePending();
        var mergeCommit = new string('f', 40);
        attempt.RecordLanded(mergeCommit, tree, 0, new string('9', 64));

        Assert.Equal(LandingAttemptStatus.Landed, attempt.Status);
        Assert.Equal(mergeCommit, attempt.MergeCommitSha);
        Assert.NotNull(attempt.ExecutedAt);
    }

    [Fact]
    public void ConflictedPreview_IsTerminalUntilAnotherAttempt()
    {
        var attempt = Start();
        attempt.RecordPreview("main", new string('c', 40), null, null, null, "[\"src/app/file.cs\"]");

        Assert.Equal(LandingAttemptStatus.Conflicted, attempt.Status);
        Assert.Throws<InvalidOperationException>(attempt.MarkExecutePending);
        Assert.Throws<InvalidOperationException>(() => attempt.Fail("Again"));
    }

    [Fact]
    public void LandedUnverifiedAttempt_CanOnlyBeRevertedWhileTargetCommitIsRecorded()
    {
        var attempt = Start();
        var mergeCommit = new string('f', 40);
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLandedUnverified(mergeCommit, "LandedTreeMismatch");

        Assert.Equal(LandingAttemptStatus.LandedUnverified, attempt.Status);
        Assert.Equal(mergeCommit, attempt.MergeCommitSha);
        attempt.MarkRevertPending();
        var revertCommit = new string('1', 40);
        attempt.RecordReverted(revertCommit);

        Assert.Equal(LandingAttemptStatus.Reverted, attempt.Status);
        Assert.Equal(revertCommit, attempt.RevertCommitSha);
    }

    [Fact]
    public void ExecuteCannotBeRepeatedOrLandedWithDifferentTree()
    {
        var attempt = Start();
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();

        Assert.Throws<InvalidOperationException>(() =>
            attempt.RecordLanded(new string('f', 40), new string('1', 40), 0, new string('9', 64)));
        attempt.RecordLanded(new string('f', 40), new string('d', 40), 0, new string('9', 64));
        Assert.Throws<InvalidOperationException>(attempt.MarkExecutePending);
    }

    [Fact]
    public void RevertedUnverifiedAttempt_IsTerminalAndRetainsRevertCommit()
    {
        var attempt = Start();
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(new string('f', 40), new string('d', 40), 0, new string('9', 64));
        attempt.MarkRevertPending();

        var revertCommit = new string('1', 40);
        attempt.RecordRevertedUnverified(revertCommit, "TargetWorktreeUpdateFailed");

        Assert.Equal(LandingAttemptStatus.RevertedUnverified, attempt.Status);
        Assert.Equal(revertCommit, attempt.RevertCommitSha);
        Assert.Equal("TargetWorktreeUpdateFailed", attempt.FailureCode);
        Assert.Throws<InvalidOperationException>(attempt.MarkRevertPending);
    }

    [Fact]
    public void Start_RejectsInvalidProofAndRationale()
    {
        var args = new object[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "main", "short", new string('c', 40), new string('b', 64), "ok" };
        Assert.Throws<ArgumentException>(() => LandingAttempt.Start(
            (Guid)args[0], (Guid)args[1], (Guid)args[2], (Guid)args[3], (Guid)args[4],
            (string)args[5], (string)args[6], (string)args[7], (string)args[8], (string)args[9]));
        Assert.Throws<ArgumentException>(() => LandingAttempt.Start(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "main", new string('a', 40), new string('c', 40), new string('b', 64), "  "));
    }
}
