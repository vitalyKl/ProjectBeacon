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
    public void GitPolicySnapshot_IsPinnedBeforePreviewAndCannotBeChangedAfterDispatch()
    {
        var attempt = Start();
        const string snapshot = """
            {"autonomyLevel":2,"dirtyCheckoutBehavior":"Escalate","protectedPaths":["src/private/**"],"maxChangedFiles":50,"maxChangedLines":5000,"autoRevertMode":"OnPostCheckFail"}
            """;

        attempt.SetGitPolicySnapshot(7, snapshot);

        Assert.Equal(7, attempt.GitPolicyRevision);
        Assert.Equal(snapshot, attempt.GitPolicySnapshotJson);

        Assert.Throws<InvalidOperationException>(() =>
            attempt.SetGitPolicySnapshot(8, snapshot));

        attempt.MarkDispatchPending("Preview");

        Assert.Throws<InvalidOperationException>(() =>
            attempt.SetGitPolicySnapshot(8, "{}"));
    }

    [Fact]
    public void EscalationCode_IsMachineReadableAndCanBeCleared()
    {
        var attempt = Start();

        attempt.SetEscalationCode("ManualExecutionRequired");

        Assert.Equal("ManualExecutionRequired", attempt.EscalationCode);
        Assert.Throws<ArgumentException>(() => attempt.SetEscalationCode("manual execution required"));
        Assert.Throws<ArgumentException>(() => attempt.SetEscalationCode(new string('x', 81)));

        attempt.SetEscalationCode(null);

        Assert.Null(attempt.EscalationCode);
    }

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
    public void PreviewFallback_PinsIntegrationBranchAsActualTarget()
    {
        var attempt = Start();
        const string snapshot = """
            {"autonomyLevel":0,"dirtyCheckoutBehavior":"LandViaIntegration","protectedPaths":[],"maxChangedFiles":200,"maxChangedLines":20000,"autoRevertMode":"Off","reviewBaseBranch":"main"}
            """;
        attempt.SetGitPolicySnapshot(4, snapshot);
        attempt.MarkDispatchPending("Preview");

        attempt.RecordPreview(
            "beacon/integration", new string('c', 40), new string('d', 40),
            new string('e', 64), 0, null);

        Assert.Equal(LandingAttemptStatus.Previewed, attempt.Status);
        Assert.Equal("beacon/integration", attempt.TargetBranch);
        Assert.Equal(snapshot, attempt.GitPolicySnapshotJson);
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

    [Theory]
    [InlineData(null, true)]
    [InlineData("9a9de4e8-6f1c-4f08-a1d0-13a9dfdc7cc1", true)]
    public void GitHubMergeQueueState_IsDurableAndDistinctFromGenericUnknown(string? requestId, bool expected)
    {
        var attempt = Start();
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.MarkGitHubMergeQueued(requestId);

        Assert.Equal(expected, attempt.IsGitHubMergeQueued);
        Assert.True(attempt.TryReadGitHubMergeRequestId(out var parsedId));
        Assert.Equal(requestId, parsedId);

        attempt.MarkCommandCompletionUnknown();

        Assert.False(attempt.IsGitHubMergeQueued);
        Assert.True(attempt.TryReadGitHubMergeRequestId(out _));
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
    public void DispatchLeaseTransitions_CanBeRetriedAndClearedForEachAction()
    {
        var attempt = Start();
        attempt.SetGitPolicySnapshot(1, """{"autonomyLevel":0,"dirtyCheckoutBehavior":"Escalate","protectedPaths":[],"maxChangedFiles":200,"maxChangedLines":20000,"autoRevertMode":"Off"}""");

        attempt.MarkDispatchPending("Preview");
        Assert.Equal(LandingAttemptStatus.PreviewPending, attempt.Status);
        Assert.Equal("PreviewEnqueuePending", attempt.FailureCode);
        attempt.ClearDispatchPending("Preview");
        Assert.Null(attempt.FailureCode);

        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkDispatchPending("Execute");
        Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);
        Assert.Equal("ExecuteEnqueuePending", attempt.FailureCode);

        attempt.MarkCommandCompletionUnknown();
        Assert.Equal("CommandCompletionUnknown", attempt.FailureCode);
        attempt.MarkDispatchPending("Execute");
        attempt.ClearDispatchPending("Execute");
        Assert.Null(attempt.FailureCode);

        attempt.RecordLanded(new string('f', 40), new string('d', 40), 0, new string('9', 64));
        attempt.MarkDispatchPending("Revert");
        Assert.Equal(LandingAttemptStatus.RevertPending, attempt.Status);
        Assert.Equal("RevertEnqueuePending", attempt.FailureCode);
        attempt.ClearDispatchPending("Revert");
        Assert.Null(attempt.FailureCode);

        attempt.RecordReverted(new string('1', 40));
        Assert.Equal(LandingAttemptStatus.Reverted, attempt.Status);
        Assert.Null(attempt.FailureCode);
    }

    [Fact]
    public void UnqueuedExecuteInvalidation_ReleasesAttemptForFreshPreview()
    {
        var attempt = Start();
        attempt.SetGitPolicySnapshot(1, """{"autonomyLevel":2,"dirtyCheckoutBehavior":"Escalate","protectedPaths":[],"maxChangedFiles":200,"maxChangedLines":20000,"autoRevertMode":"Off"}""");
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();

        attempt.Fail("GitPolicyChangedSincePreview");
        attempt.SetEscalationCode("GitPolicyChangedSincePreview");

        Assert.Equal(LandingAttemptStatus.Failed, attempt.Status);
        Assert.Equal("GitPolicyChangedSincePreview", attempt.FailureCode);
        Assert.Equal("GitPolicyChangedSincePreview", attempt.EscalationCode);
    }

    [Fact]
    public void FailedRevert_CanReturnToPendingForAnExplicitSafeRetry()
    {
        var attempt = Start();
        attempt.RecordPreview("main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(new string('f', 40), new string('d', 40), 0, new string('9', 64));
        attempt.MarkRevertPending();
        attempt.Fail("TargetAdvancedBeyondLanding");

        Assert.Equal(LandingAttemptStatus.RevertFailed, attempt.Status);
        Assert.Equal("TargetAdvancedBeyondLanding", attempt.FailureCode);

        attempt.MarkDispatchPending("Revert");

        Assert.Equal(LandingAttemptStatus.RevertPending, attempt.Status);
        Assert.Equal("RevertEnqueuePending", attempt.FailureCode);
    }

    [Fact]
    public void GitHubMergeQueue_KeepsExecutePendingAndSupportsMissingRequestUuid()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var withRequestId = Start();
        withRequestId.RecordPreview(
            "main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        withRequestId.MarkExecutePending();
        withRequestId.MarkGitHubMergeQueued(requestId);

        Assert.Equal(LandingAttemptStatus.ExecutePending, withRequestId.Status);
        Assert.Equal("GitHubMergeQueued:" + requestId, withRequestId.FailureCode);
        Assert.True(withRequestId.TryReadGitHubMergeRequestId(out var storedRequestId));
        Assert.Equal(requestId, storedRequestId);

        var alreadyEnqueued = Start();
        alreadyEnqueued.RecordPreview(
            "main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        alreadyEnqueued.MarkExecutePending();
        alreadyEnqueued.MarkGitHubMergeQueued(null);

        Assert.Equal(LandingAttemptStatus.ExecutePending, alreadyEnqueued.Status);
        Assert.Equal("GitHubMergeQueued", alreadyEnqueued.FailureCode);
        Assert.True(alreadyEnqueued.TryReadGitHubMergeRequestId(out var missingRequestId));
        Assert.Null(missingRequestId);
    }

    [Fact]
    public void UnknownExecuteAcknowledgement_CanBeReconciledAgainstTheExactRemotePullRequest()
    {
        var attempt = Start();
        attempt.RecordPreview(
            "main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.MarkCommandCompletionUnknown();

        Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);
        Assert.True(attempt.TryReadGitHubMergeRequestId(out var requestId));
        Assert.Null(requestId);
    }

    [Fact]
    public void GitHubMergeQueue_RejectsInvalidRequestUuid()
    {
        var attempt = Start();
        attempt.RecordPreview(
            "main", new string('c', 40), new string('d', 40), new string('e', 64), 0, null);
        attempt.MarkExecutePending();

        Assert.Throws<ArgumentException>(() => attempt.MarkGitHubMergeQueued("not-a-guid"));
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
