namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

public sealed class ReleaseEntityTests
{
    [Fact]
    public void Release_UsesIndependentLifecycleAndCannotBePublishedByGenericTransition()
    {
        var release = Release.Create(
            "2026.12", "December release", Guid.NewGuid(), "0.7.5.9", "main", 1);

        Assert.Equal(ReleaseStatus.Planned, release.Status);
        release.TransitionTo(ReleaseStatus.InProgress);
        release.TransitionTo(ReleaseStatus.Candidate);

        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.Published));

        release.TransitionTo(ReleaseStatus.InProgress);
        Assert.Equal(ReleaseStatus.InProgress, release.Status);
        release.TransitionTo(ReleaseStatus.Cancelled);
        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.Planned));
    }

    [Fact]
    public void Release_CanBePublishedOnlyFromCandidateByPublicationMethod()
    {
        var release = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        release.TransitionTo(ReleaseStatus.Candidate);

        var publishedAt = DateTime.UtcNow;
        release.MarkPublished(publishedAt);

        Assert.Equal(ReleaseStatus.Published, release.Status);
        Assert.Equal(publishedAt, release.PublishedAt);
        Assert.Throws<InvalidOperationException>(
            () => release.Update(name: "changed"));
        Assert.Throws<InvalidOperationException>(
            () => release.TransitionTo(ReleaseStatus.InProgress));
    }

    [Fact]
    public void Release_CannotPublishFromPlannedOrInProgress()
    {
        var planned = Release.Create("planned", null, Guid.NewGuid(), null, "main", 1);
        Assert.Throws<InvalidOperationException>(() => planned.MarkPublished(DateTime.UtcNow));

        planned.TransitionTo(ReleaseStatus.InProgress);
        Assert.Throws<InvalidOperationException>(() => planned.MarkPublished(DateTime.UtcNow));
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
