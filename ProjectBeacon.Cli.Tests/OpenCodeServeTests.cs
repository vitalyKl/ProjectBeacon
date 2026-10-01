namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class OpenCodeServeTests : IDisposable
{
    private readonly string _dir;

    public OpenCodeServeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-oc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Tick_SkipRealProcess_IsHealthy()
    {
        await using var serve = new ClientOpenCodeServe { SkipRealProcess = true };
        await serve.TickAsync(_dir, CancellationToken.None);
        Assert.True(serve.Status.Healthy);
        Assert.Equal(1, serve.StartCount);
        await serve.TickAsync(_dir, CancellationToken.None);
        Assert.Equal(1, serve.StartCount);
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        await serve.TickAsync(other, CancellationToken.None);
        Assert.Equal(2, serve.StartCount);
    }

    [Fact]
    public async Task CreateSession_SkipRealProcess_ReturnsTestId()
    {
        await using var serve = new ClientOpenCodeServe { SkipRealProcess = true };
        await serve.TickAsync(_dir, CancellationToken.None);
        var id = await serve.CreateSessionAsync("Chat", CancellationToken.None);
        Assert.Equal("oc_test", id);
        var parts = await serve.ListPartsAsync(id, CancellationToken.None);
        Assert.Contains(parts, p => p.Role == "assistant");
    }

    [Fact]
    public async Task ListParts_ParsesTextReasoningAndTool()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) =>
            {
                const string json = """
                    [{"info":{"role":"assistant"},"parts":[
                      {"id":"t1","type":"text","text":"hello"},
                      {"id":"r1","type":"reasoning","text":"think"},
                      {"id":"c1","type":"tool","tool":"read","state":{"status":"completed","output":"src/a.cs"}}
                    ]}]
                    """;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };

        var parts = await serve.ListPartsAsync("s", CancellationToken.None);
        Assert.Equal(3, parts.Count);
        Assert.Equal("text", parts[0].Kind);
        Assert.Equal("hello", parts[0].Body);
        Assert.Equal("reasoning", parts[1].Kind);
        Assert.Equal("tool", parts[2].Kind);
        Assert.Equal("read\ncompleted\nsrc/a.cs", parts[2].Body);
    }
}
