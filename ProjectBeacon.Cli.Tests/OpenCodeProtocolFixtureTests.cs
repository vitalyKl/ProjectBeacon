namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text;
using ProjectBeacon.Cli.Client;

public sealed class OpenCodeProtocolFixtureTests
{
    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task V1_GlobalHealth_FixtureIsAccepted()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/api/info"
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(HttpStatusCode.OK, ProtocolFixtureFiles.Read("OpenCode/v1-global-health.json")))
        };

        await serve.PollAsync(CancellationToken.None);

        Assert.True(serve.Status.Healthy);
        Assert.Equal("1.19.3", serve.Status.Version);
    }

    [Fact]
    public async Task V2_Info_FixtureIsAccepted()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(
                Json(HttpStatusCode.OK, ProtocolFixtureFiles.Read("OpenCode/v2-info.json")))
        };
        serve.HandleStdoutLine("server password fixture-password");

        await serve.PollAsync(CancellationToken.None);

        Assert.True(serve.Status.Healthy);
        Assert.Equal("2.1.0", serve.Status.Version);
    }

    [Fact]
    public async Task V1_MessageList_FixtureParsesTextReasoningAndToolParts()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(
                Json(HttpStatusCode.OK, ProtocolFixtureFiles.Read("OpenCode/v1-messages.json")))
        };

        var parts = await serve.ListPartsAsync("ses_fixture", CancellationToken.None);

        Assert.Equal(3, parts.Count);
        Assert.Equal("text", parts[0].Kind);
        Assert.Equal("hello", parts[0].Body);
        Assert.Equal("reasoning", parts[1].Kind);
        Assert.Equal("think", parts[1].Body);
        Assert.Equal("tool", parts[2].Kind);
    }

    [Fact]
    public async Task V1_SessionCreate_FixtureReturnsSessionId()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(
                Json(HttpStatusCode.OK, ProtocolFixtureFiles.Read("OpenCode/v1-session-created.json")))
        };

        var sessionId = await serve.CreateSessionAsync("Fixture", CancellationToken.None);

        Assert.Equal("oc_fixture", sessionId);
    }

    [Fact]
    public async Task V2_SessionCreate_FixtureReturnsWrappedSessionId()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(
                Json(HttpStatusCode.OK, ProtocolFixtureFiles.Read("OpenCode/v2-session-created.json")))
        };
        serve.HandleStdoutLine("server password fixture-password");

        var sessionId = await serve.CreateSessionAsync("Fixture", CancellationToken.None);

        Assert.Equal("ses_fixture", sessionId);
    }
}
