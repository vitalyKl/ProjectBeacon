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
    public void Complete_WithTarget_IsCheckProof()
    {
        var run = ReviewRun.Start(Guid.NewGuid(), Guid.NewGuid(), ReviewerType.Agent, Guid.NewGuid());
        run.Complete("ok", "check:exit0");

        Assert.True(run.IsCheckProof());
        Assert.Equal("check:exit0", run.ArtifactRef);
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
