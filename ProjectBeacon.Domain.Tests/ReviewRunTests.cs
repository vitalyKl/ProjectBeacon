namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Evals;
using ProjectBeacon.Domain.Enums;

public sealed class ReviewRunTests
{
    [Fact]
    public void Create_IsNotCheckProof()
    {
        var run = ReviewRun.Create(Guid.NewGuid(), Guid.NewGuid(), "transcript");

        Assert.Equal(ReviewRunStatus.Completed, run.Status);
        Assert.False(run.IsCheckProof());
    }

    [Fact]
    public void Complete_WithoutPinnedCommandAndGitRevision_IsNotLandingProof()
    {
        var run = ReviewRun.Start(Guid.NewGuid(), Guid.NewGuid(), ReviewerType.Agent, Guid.NewGuid());
        run.Complete("ok", "check:exit0");

        Assert.True(run.IsCheckProof());
        Assert.False(run.IsLandingProof());
    }

    [Fact]
    public void CompleteCheck_WithPinnedCommandAndGitRevision_IsLandingProof()
    {
        var run = ReviewRun.Start(Guid.NewGuid(), Guid.NewGuid(), ReviewerType.Agent, Guid.NewGuid());
        var commandHash = new string('a', 64);
        var sourceSha = new string('b', 40);
        var treeSha = new string('c', 40);
        run.SetCheckCommandHash(commandHash);
        var deviceId = Guid.NewGuid();
        run.CompleteCheck("ok", "check:exit0", sourceSha, treeSha, deviceId, "main");

        Assert.True(run.IsCheckProof());
        Assert.True(run.IsLandingProof());
        Assert.Equal(commandHash, run.CheckCommandHash);
        Assert.Equal(sourceSha, run.CheckedSourceSha);
        Assert.Equal(treeSha, run.CheckedTreeSha);
        Assert.Equal(deviceId, run.CheckedDeviceId);
        Assert.Equal("main", run.CheckedBaseBranch);
        Assert.Equal("check:exit0", run.ArtifactRef);
    }

    [Fact]
    public void SetCheckCommandHash_RequiresStartedRunAndSha256()
    {
        var completed = ReviewRun.Create(Guid.NewGuid(), Guid.NewGuid(), "transcript");

        Assert.Throws<InvalidOperationException>(() => completed.SetCheckCommandHash(new string('a', 64)));

        var started = ReviewRun.Start(Guid.NewGuid(), Guid.NewGuid(), ReviewerType.Agent, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => started.SetCheckCommandHash("not-a-hash"));
    }

    [Fact]
    public void Fail_SetsFailed()
    {
        var run = ReviewRun.Start(Guid.NewGuid(), Guid.NewGuid(), ReviewerType.Human, Guid.NewGuid());
        run.Fail("tests failed");

        Assert.Equal(ReviewRunStatus.Failed, run.Status);
        Assert.False(run.IsCheckProof());
        Assert.NotNull(run.CompletedAt);
    }
}
