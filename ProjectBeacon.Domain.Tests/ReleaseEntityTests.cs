namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

public sealed class ReleaseEntityTests
{
    [Fact]
    public void Release_UsesIndependentLifecycleAndCandidateIsReservedForReleasePrepare()
    {
        var release = Release.Create(
            "2026.12", "December release", Guid.NewGuid(), "0.7.5.9", "main", 1);

        Assert.Equal(ReleaseStatus.Planned, release.Status);
        release.TransitionTo(ReleaseStatus.InProgress);
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.Candidate));
        Assert.Equal(ReleaseStatus.InProgress, release.Status);
        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.Published));

        release.TransitionTo(ReleaseStatus.Cancelled);
        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.Planned));
        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.InProgress));
    }

    [Fact]
    public void Release_CannotPublishBeforeReleasePrepareHasCreatedCandidate()
    {
        var planned = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);
        Assert.Throws<InvalidOperationException>(() => planned.MarkPublished(DateTime.UtcNow));

        planned.TransitionTo(ReleaseStatus.InProgress);
        Assert.Throws<InvalidOperationException>(() => planned.MarkPublished(DateTime.UtcNow));
        Assert.Null(planned.PublishedAt);
    }

    [Fact]
    public void Release_RevisionChangesOnlyWhenPlanningFieldsChange()
    {
        var release = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);

        release.Update(name: release.Name);
        Assert.Equal(1, release.Revision);
        Assert.Null(release.UpdatedAt);

        release.Update(targetBranch: "release/1.0");
        Assert.Equal(2, release.Revision);
        Assert.Equal("release/1.0", release.TargetBranch);
        Assert.NotNull(release.UpdatedAt);
    }

    [Fact]
    public void CancelledReleaseCannotBeEdited()
    {
        var release = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);
        release.TransitionTo(ReleaseStatus.Cancelled);

        Assert.Throws<InvalidOperationException>(() => release.Update(description: "Changed"));
    }

    [Fact]
    public void Milestone_ReleaseAssociationIsOptionalAndRevisioned()
    {
        var projectId = Guid.NewGuid();
        var milestone = Milestone.Create("Prepare", null, projectId, 1);
        var releaseId = Guid.NewGuid();
        var initialRevision = milestone.Revision;

        milestone.AssignRelease(releaseId);
        Assert.Equal(releaseId, milestone.ReleaseId);
        Assert.Equal(initialRevision + 1, milestone.Revision);

        milestone.AssignRelease(null);
        Assert.Null(milestone.ReleaseId);
        Assert.Equal(initialRevision + 2, milestone.Revision);
    }
}
