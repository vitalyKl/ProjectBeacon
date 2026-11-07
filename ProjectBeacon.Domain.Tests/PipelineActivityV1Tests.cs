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
        Assert.Throws<JsonException>(() => PipelineActivityV1.Parse("{"));
    }

    [Fact]
    public void PreserveAnsweredForms_RetainsCompletedAnswersMissingFromCurrentPoll()
    {
        var previous = PipelineActivityV1.Parse(
            """{"forms":[{"id":"answered","title":"Keep","fields":[],"answer":{"choice":"yes"}},{"id":"pending","fields":[]}]}""");
        var current = PipelineActivityV1.Parse(
            """{"state":"waiting-for-user","forms":[{"id":"pending","title":"New","fields":[]}]}""");

        var merged = current.PreserveAnsweredForms(previous);

        Assert.Equal(["pending", "answered"], merged.Forms.Select(form => form.Id));
        Assert.Equal("yes", merged.Forms[1].Answer?.GetProperty("choice").GetString());
    }

    [Fact]
    public void TryParse_ReturnsFalseForMalformedLegacyData()
    {
        Assert.False(PipelineActivityV1.TryParse("not-json", out var parsed));
        Assert.Null(parsed);
    }
}
