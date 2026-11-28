namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

public sealed class ReleaseTests
{
    private static Release CreateRelease() =>
        Release.Create("1.0 Release", "Initial release", Guid.NewGuid(), "1.0.0", "main", 1);

    [Fact]
    public void Create_StartsPlannedWithProjectVersionAndBranchTarget()
    {
        var projectId = Guid.NewGuid();
        var release = Release.Create("1.0 Release", "Initial release", projectId, "1.0.0", "main", 2);

        Assert.NotEqual(Guid.Empty, release.Id);
        Assert.Equal(projectId, release.ProjectId);
        Assert.Equal("1.0 Release", release.Name);
        Assert.Equal("Initial release", release.Description);
        Assert.Equal("1.0.0", release.TargetVersion);
        Assert.Equal("main", release.TargetBranch);
        Assert.Equal(2, release.Order);
        Assert.Equal(ReleaseStatus.Planned, release.Status);
        Assert.Equal(1, release.Revision);
        Assert.Null(release.PublishedAt);
    }

    [Fact]
    public void Lifecycle_CandidateCanOnlyBeCreatedByReleasePrepare()
    {
        var release = CreateRelease();

        release.TransitionTo(ReleaseStatus.InProgress);
        Assert.Equal(ReleaseStatus.InProgress, release.Status);
        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.Candidate));
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        release.TransitionTo(ReleaseStatus.Cancelled);
        Assert.Equal(ReleaseStatus.Cancelled, release.Status);
        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.Planned));
        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.InProgress));
    }

    [Fact]
    public void Published_CannotBeReachedThroughGenericTransitionOrBeforeCandidatePreparation()
    {
        var release = CreateRelease();

        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.Published));
        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.Published));

        release.TransitionTo(ReleaseStatus.InProgress);
        Assert.Throws<InvalidOperationException>(() => release.TransitionTo(ReleaseStatus.Published));
        Assert.Equal(ReleaseStatus.InProgress, release.Status);
        Assert.Null(release.PublishedAt);
    }

    [Fact]
    public void CancelledReleaseCannotBeEdited()
    {
        var release = CreateRelease();
        release.TransitionTo(ReleaseStatus.Cancelled);

        Assert.Throws<InvalidOperationException>(() => release.Update(description: "Changed"));
    }

    [Fact]
    public void Update_IncrementsRevisionOnlyWhenFieldsChange()
    {
        var release = CreateRelease();

        release.Update(name: release.Name);
        Assert.Equal(1, release.Revision);
        Assert.Null(release.UpdatedAt);

        release.Update(targetBranch: "release/1.0");
        Assert.Equal(2, release.Revision);
        Assert.Equal("release/1.0", release.TargetBranch);
        Assert.NotNull(release.UpdatedAt);
    }
}
