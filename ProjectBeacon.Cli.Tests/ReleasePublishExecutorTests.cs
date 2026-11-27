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
        Assert.False(Validate(tagObject: TagObject() + "\nProjectBeacon-Release: " + ReleaseId.ToString("D")));
    }
}
