namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class ReleasePublishExecutorTests
{
    private static readonly Guid ReleaseId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid CandidateId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string TagName = "v1.2.3";
    private const string Version = "1.2.3";
    private static readonly string CandidateSha = new('a', 40);

    private static string TagObject(
        string? tagName = null,
        string? targetSha = null,
        string? releaseLine = null,
        string? candidateLine = null,
        string? candidateShaLine = null,
        string? versionLine = null)
    {
        return string.Join("\n",
            "object " + (targetSha ?? CandidateSha),
            "type commit",
            "tag " + (tagName ?? TagName),
            "tagger ProjectBeacon Tests <tests@projectbeacon.local> 1795780800 +0000",
            "",
            "ProjectBeacon Release " + Version,
            releaseLine ?? "ProjectBeacon-Release: " + ReleaseId.ToString("D"),
            candidateLine ?? "ProjectBeacon-Candidate: " + CandidateId.ToString("D"),
            candidateShaLine ?? "ProjectBeacon-Candidate-SHA: " + CandidateSha,
            versionLine ?? "ProjectBeacon-Version: " + Version);
    }

    private static bool Validate(
        string objectType = "tag",
        string? tagObject = null,
        string? targetSha = null,
        string? tagName = null) =>
        ReleasePublishExecutor.HasExpectedAnnotatedTagEvidence(
            objectType,
            tagObject ?? TagObject(),
            targetSha ?? CandidateSha,
            tagName ?? TagName,
            ReleaseId,
            CandidateId,
            CandidateSha,
            Version);

    [Fact]
    public void AnnotatedTagEvidence_AcceptsExactReleaseCandidateAndTarget()
    {
        Assert.True(Validate());
    }

    [Fact]
    public void AnnotatedTagEvidence_RejectsDifferentTargetTagNameAndLightweightTag()
    {
        Assert.False(Validate(targetSha: new string('b', 40)));
        Assert.False(Validate(tagName: "v1.2.4"));
        Assert.False(Validate(objectType: "commit"));
    }

    [Fact]
    public void AnnotatedTagEvidence_RejectsMissingAlteredOrDuplicatedIdentityLines()
    {
        Assert.False(Validate(tagObject: TagObject(
            releaseLine: "ProjectBeacon-Release: " + Guid.NewGuid().ToString("D"))));
        Assert.False(Validate(tagObject: TagObject(
            candidateLine: "ProjectBeacon-Candidate: " + Guid.NewGuid().ToString("D"))));
        Assert.False(Validate(tagObject: TagObject(
            candidateShaLine: "ProjectBeacon-Candidate-SHA: " + new string('b', 40))));
        Assert.False(Validate(tagObject: TagObject(
            versionLine: "ProjectBeacon-Version: 1.2.4")));
        Assert.False(Validate(tagObject: TagObject(
            releaseLine: "ProjectBeacon-Release: " + ReleaseId.ToString("D") + "-unexpected")));
        Assert.False(Validate(tagObject: TagObject(
            releaseLine: "ProjectBeacon-Release: " + ReleaseId.ToString("D") + " ")));
        Assert.False(Validate(tagObject: TagObject(
            releaseLine: "ProjectBeacon-Release: " + ReleaseId.ToString("D") + "\r")));
        Assert.False(Validate(tagObject: TagObject() + "\nProjectBeacon-Release: " + ReleaseId.ToString("D")));
    }

    [Fact]
    public void WorkflowPreflight_RequiresSuccessfulGitHubActionsReadBeforeTagPush()
    {
        Assert.Null(ReleasePublishExecutor.ValidateWorkflowPreflight(0, false, "[]", ""));

        Assert.Contains(
            "invalid workflow-run list",
            ReleasePublishExecutor.ValidateWorkflowPreflight(0, false, "{}", "") ?? "",
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "could not list",
            ReleasePublishExecutor.ValidateWorkflowPreflight(1, false, "[]", "HTTP 403") ?? "",
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "timed out",
            ReleasePublishExecutor.ValidateWorkflowPreflight(0, true, "[]", "") ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousTagPush_OnlyReportsPreTagWhenCompleteRemoteEvidenceProvesCandidateTagAbsent()
    {
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            false, true, false, false, null, CandidateSha));
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            true, false, false, false, null, CandidateSha));
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, true, true, CandidateSha, CandidateSha));

        // An incomplete annotated-tag listing is not proof that the Candidate tag is absent.
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, true, false, null, CandidateSha));
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, false, true, CandidateSha, CandidateSha));
        Assert.True(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, true, true, null, CandidateSha));

        Assert.False(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, false, false, null, CandidateSha));
        Assert.False(ReleasePublishExecutor.MayHavePublishedTag(
            true, true, true, true, new string('b', 40), CandidateSha));
    }

}
