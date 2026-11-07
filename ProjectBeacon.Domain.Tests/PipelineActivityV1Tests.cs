namespace ProjectBeacon.Domain.Tests;

using System.Text.Json;
using ProjectBeacon.Domain.Common;

public sealed class PipelineActivityV1Tests
{
    [Fact]
    public void Parse_LegacySnapshotWithoutVersion_DefaultsToV1()
    {
        const string legacy = """{"state":"running","activity":"Reading files","parts":[{"kind":"text","role":"assistant","body":"hello","externalId":"part-1","complete":true}]}""";

        var parsed = PipelineActivityV1.Parse(legacy);

        Assert.Equal(PipelineActivityV1.CurrentVersion, parsed.Version);
        Assert.Equal("running", parsed.State);
        Assert.Equal("Reading files", parsed.Activity);
        Assert.Single(parsed.Parts);
        Assert.Equal("part-1", parsed.Parts[0].ExternalId);
        Assert.Contains("\"version\":1", parsed.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnsupportedVersionsAndNonObjectPayloads()
    {
        Assert.Throws<JsonException>(() => PipelineActivityV1.Parse("""{"version":2}"""));
        Assert.Throws<JsonException>(() => PipelineActivityV1.Parse("[]"));
        Assert.ThrowsAny<JsonException>(() => PipelineActivityV1.Parse("{"));
    }

    [Fact]
    public void PreserveAnsweredForms_RetainsCompletedAnswersMissingFromCurrentPoll()
    {
        var previous = PipelineActivityV1.Parse(
            """{"forms":[{"id":"answered","title":"Keep","fields":[],"answer":{"choice":"yes"}},{"id":"pending","fields":[]}]}""");
        var current = PipelineActivityV1.Parse(
            """{"state":"waiting-for-user","forms":[{"id":"pending","title":"New","fields":[]}]}""");

        var merged = current.PreserveAnsweredForms(previous);

        Assert.Equal(new[] { "pending", "answered" }, merged.Forms.Select(form => form.Id));
        Assert.Equal("yes", merged.Forms[1].Answer?.GetProperty("choice").GetString());
    }

    [Fact]
    public void Parse_PreservesFormReplyContextAndUnknownProperties()
    {
        const string json = """
            {
              "externalSessionId": "session-1",
              "deviceId": "0c0e8c6f-81bd-49d0-bd8e-6daca86acb10",
              "commandId": "aa35ff1c-10de-4857-819a-67af22a6d221",
              "updatedAt": "2026-11-07T12:00:00+00:00",
              "vendorMetadata": { "source": "runtime-v2" },
              "forms": [
                {
                  "id": "confirm",
                  "title": "Confirm",
                  "fields": [],
                  "answer": { "confirmed": true },
                  "vendorField": "preserve-me"
                }
              ]
            }
            """;

        var parsed = PipelineActivityV1.Parse(json);
        var roundTripped = PipelineActivityV1.Parse(parsed.ToJson());

        Assert.Equal("session-1", roundTripped.ExternalSessionId);
        Assert.Equal("0c0e8c6f-81bd-49d0-bd8e-6daca86acb10", roundTripped.DeviceId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T12:00:00+00:00"), roundTripped.UpdatedAt);
        Assert.Equal("runtime-v2",
            roundTripped.AdditionalProperties!["vendorMetadata"].GetProperty("source").GetString());
        Assert.True(roundTripped.Forms[0].Answer!.Value.GetProperty("confirmed").GetBoolean());
        Assert.Equal("preserve-me",
            roundTripped.Forms[0].AdditionalProperties!["vendorField"].GetString());
    }

    [Fact]
    public void TryParse_ReturnsFalseForMalformedLegacyData()
    {
        Assert.False(PipelineActivityV1.TryParse("not-json", out var parsed));
        Assert.Null(parsed);
    }
}
