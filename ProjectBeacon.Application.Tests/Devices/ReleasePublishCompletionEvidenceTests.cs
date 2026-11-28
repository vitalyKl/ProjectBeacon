namespace ProjectBeacon.Application.Tests.Devices;

using System.Text.Json.Nodes;
using Application.Devices;

public sealed class ReleasePublishCompletionEvidenceTests
{
    private const string ProjectId = "11111111-1111-4111-8111-111111111111";
    private const string ReleaseId = "22222222-2222-4222-8222-222222222222";
    private const string CandidateId = "33333333-3333-4333-8333-333333333333";
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Version = "1.2.3";
    private const string TagName = "v1.2.3";

    private static JsonObject Payload() => new()
    {
        ["projectId"] = ProjectId,
        ["releaseId"] = ReleaseId,
        ["candidateId"] = CandidateId,
        ["version"] = Version,
        ["tagName"] = TagName,
        ["candidateSha"] = CandidateSha
    };

    private static JsonObject Result(bool? tagPushed) => new()
    {
        ["action"] = "Publish",
        ["projectId"] = ProjectId,
        ["releaseId"] = ReleaseId,
        ["candidateId"] = CandidateId,
        ["version"] = Version,
        ["tagName"] = TagName,
        ["candidateSha"] = CandidateSha,
        ["tagPushed"] = tagPushed is null ? null : JsonValue.Create(tagPushed.Value)
    };

    [Fact]
    public void TagPushedEvidence_OnlyAcceptsExplicitFalseForExactPublishIdentity()
    {
        var payload = Payload();

        Assert.False(ReleasePublishCompletion.ReadTagPushedEvidence(payload, Result(false).ToJsonString()));
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, Result(true).ToJsonString()));
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, Result(null).ToJsonString()));
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, null));
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, "{}"));
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, "{malformed"));

        var mismatched = Result(false);
        mismatched["candidateId"] = Guid.NewGuid().ToString("D");
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, mismatched.ToJsonString()));

        var wrongAction = Result(false);
        wrongAction["action"] = "Prepare";
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, wrongAction.ToJsonString()));

        var changedSha = Result(false);
        changedSha["candidateSha"] = new string('b', 40);
        Assert.True(ReleasePublishCompletion.ReadTagPushedEvidence(payload, changedSha.ToJsonString()));
    }
}
