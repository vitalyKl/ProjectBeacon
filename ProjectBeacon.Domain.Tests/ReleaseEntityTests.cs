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
    public void CandidateTransition_RequiresPinnedPrepareAndVerifyEvidenceForExactRelease()
    {
        var projectId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var prepareCommandId = Guid.NewGuid();
        var verifyCommandId = Guid.NewGuid();
        var release = Release.Create("1.0", null, projectId, "1.0.0", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        release.BeginPreparation(deviceId, candidateId, prepareCommandId, """{"action":"Prepare"}""");

        Assert.Throws<ArgumentNullException>(() => release.MarkCandidate(null!));
        Assert.Throws<InvalidOperationException>(() => release.BeginVerification(Guid.NewGuid(), verifyCommandId));
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        release.RecordPrepareSuccess(prepareCommandId, """{"preparedCommitSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}""");
        release.BeginVerification(deviceId, verifyCommandId);

        var otherProjectSnapshot = ReleaseCandidateSnapshot.Create(
            Guid.NewGuid(), release.Id, candidateId, deviceId, "1.0.0", "v1.0.0", "main",
            Sha('a'), Sha('b'), Sha('c'), Sha('d'),
            1, release.Revision, new string('e', 64),
            """{"schemaVersion":1}""", DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => release.MarkCandidate(otherProjectSnapshot));
        Assert.Equal(ReleaseStatus.InProgress, release.Status);

        var snapshot = ReleaseCandidateSnapshot.Create(
            projectId, release.Id, candidateId, deviceId, "1.0.0", "v1.0.0", "main",
            Sha('a'), Sha('b'), Sha('c'), Sha('d'),
            2, release.Revision, new string('e', 64),
            """{"schemaVersion":1}""", DateTime.UtcNow);

        var previousRevision = release.Revision;
        release.MarkCandidate(snapshot);

        Assert.Equal(ReleaseStatus.Candidate, release.Status);
        Assert.Equal(ReleasePreparationStatus.Candidate, release.PreparationStatus);
        Assert.Equal(deviceId, release.PreparationDeviceId);
        Assert.Same(snapshot, release.CandidateSnapshot);
        Assert.Equal(previousRevision + 1, release.Revision);
        Assert.Throws<InvalidOperationException>(() => release.MarkCandidate(snapshot));
        Assert.Throws<InvalidOperationException>(() => release.Update(description: "Cannot edit candidate"));
    }

    [Fact]
    public void ReleasePrepare_RetryMustKeepWorkstationCandidateAndPayloadPinned()
    {
        var release = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var deviceId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var payload = """{"action":"Prepare","candidateId":"candidate"}""";
        release.BeginPreparation(deviceId, candidateId, Guid.NewGuid(), payload);
        release.RecordPrepareFailure(release.PreparationCommandId!.Value, "workstation failure");

        Assert.Throws<InvalidOperationException>(
            () => release.BeginPreparation(Guid.NewGuid(), candidateId, Guid.NewGuid(), payload));
        Assert.Throws<InvalidOperationException>(
            () => release.BeginPreparation(deviceId, Guid.NewGuid(), Guid.NewGuid(), payload));
        Assert.Throws<InvalidOperationException>(
            () => release.BeginPreparation(deviceId, candidateId, Guid.NewGuid(), """{"action":"Prepare","candidateId":"other"}"""));

        release.BeginPreparation(deviceId, candidateId, Guid.NewGuid(), payload);
        Assert.Equal(ReleasePreparationStatus.PrepareQueued, release.PreparationStatus);
        Assert.Equal(deviceId, release.PreparationDeviceId);
        Assert.Equal(candidateId, release.CandidateId);
        Assert.Equal(payload, release.PreparationPayloadJson);
    }

    [Fact]
    public void ReleaseVerifyFailureCanRetryOnlyOnThePinnedWorkstation()
    {
        var release = Release.Create("1.0", null, Guid.NewGuid(), "1.0.0", "main", 1);
        release.TransitionTo(ReleaseStatus.InProgress);
        var deviceId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var prepareCommand = Guid.NewGuid();
        release.BeginPreparation(deviceId, candidateId, prepareCommand, """{"action":"Prepare"}""");
        release.RecordPrepareSuccess(prepareCommand, """{"preparedCommitSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}""");
        var verifyCommand = Guid.NewGuid();
        release.BeginVerification(deviceId, verifyCommand);
        release.RecordVerificationFailure(verifyCommand, "target branch has not landed the prepared commit");

        Assert.Throws<InvalidOperationException>(() => release.BeginVerification(Guid.NewGuid(), Guid.NewGuid()));
        release.BeginVerification(deviceId, Guid.NewGuid());
        Assert.Equal(ReleasePreparationStatus.VerifyQueued, release.PreparationStatus);
    }

    [Fact]
    public void CandidateSnapshot_RejectsInvalidShaHashRevisionAndJson()
    {
        var projectId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var sha = new string('a', 40);
        var hash = new string('b', 64);
        var timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => ReleaseCandidateSnapshot.Create(
            projectId, releaseId, candidateId, deviceId, "1.0.0", "v1.0.0", "main", "bad", sha, sha, sha,
            1, 1, hash, """{"schemaVersion":1}""", timestamp));
        Assert.Throws<ArgumentException>(() => ReleaseCandidateSnapshot.Create(
            projectId, releaseId, candidateId, deviceId, "1.0.0", "v1.0.0", "main", sha, sha, sha, sha,
            0, 1, hash, """{"schemaVersion":1}""", timestamp));
        Assert.Throws<ArgumentException>(() => ReleaseCandidateSnapshot.Create(
            projectId, releaseId, candidateId, deviceId, "1.0.0", "v1.0.0", "main", sha, sha, sha, sha,
            1, 1, "bad", """{"schemaVersion":1}""", timestamp));
        Assert.Throws<ArgumentException>(() => ReleaseCandidateSnapshot.Create(
            projectId, releaseId, candidateId, deviceId, "1.0.0", "v1.0.0", "main", sha, sha, sha, sha,
            1, 1, hash, """[]""", timestamp));
        Assert.Throws<ArgumentException>(() => ReleaseCandidateSnapshot.Create(
            projectId, releaseId, candidateId, deviceId, "1.0.0", "v1.0.0", "main", sha, sha, sha, sha,
            1, 1, hash, """{"schemaVersion":1}""", DateTime.Now));
    }

    private static string Sha(char value) => new(value, 40);

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
