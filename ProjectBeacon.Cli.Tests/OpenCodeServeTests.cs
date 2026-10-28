namespace ProjectBeacon.Cli.Tests;

using System.Net;
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

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage StatusCode(HttpStatusCode code) =>
        new(code) { Content = new StringContent("") };

    private static void MarkV2(ClientOpenCodeServe serve) => serve.HandleStdoutLine("server password secret-pw");

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
                      {"id":"c1","type":"tool","tool":"read","state":{"status":"completed","output":"src/a.cs","time":{"start":1000,"end":2500}}}
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
        Assert.Equal(1500, parts[2].DurationMs);
    }

    [Fact]
    public async Task HandleStdoutLine_CapturesServerPasswordOnly()
    {
        await using var serve = new ClientOpenCodeServe();
        Assert.Null(serve.ServerPassword);
        serve.HandleStdoutLine("opencode server listening on http://127.0.0.1:4096");
        Assert.Null(serve.ServerPassword);
        serve.HandleStdoutLine("  SERVER PASSWORD abc123 ");
        Assert.Equal("abc123", serve.ServerPassword);
        serve.HandleStdoutLine("server password other");
        Assert.Equal("other", serve.ServerPassword);
    }

    [Fact]
    public async Task PollAsync_V2_UsesApiInfo()
    {
        string? path = null;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(JsonResponse("""{"version":"2.1.0","pid":42}"""));
            }
        };
        MarkV2(serve);
        await serve.PollAsync(CancellationToken.None);
        Assert.Equal("/api/info", path);
        Assert.True(serve.Status.Healthy);
        Assert.Equal("2.1.0", serve.Status.Version);
    }

    [Fact]
    public async Task PollAsync_V2_WaitsForPasswordOn401()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(StatusCode(HttpStatusCode.Unauthorized)),
            TestForceRunning = true
        };
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("waiting for server password", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollAsync_V2_ForeignServerOnPortWhenOwnProcessDead()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(StatusCode(HttpStatusCode.Unauthorized))
        };
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("may occupy the port", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollAsync_V2_RejectedCredentialsAfterPassword()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(StatusCode(HttpStatusCode.Unauthorized)),
            TestForceRunning = true
        };
        MarkV2(serve);
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("rejected credentials", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://127.0.0.1:4096", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9-char password", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollAsync_V1_FallsBackToGlobalHealth()
    {
        var paths = new List<string>();
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                var p = request.RequestUri!.AbsolutePath;
                paths.Add(p);
                return p == "/api/info"
                    ? Task.FromResult(StatusCode(HttpStatusCode.NotFound))
                    : Task.FromResult(JsonResponse("""{"healthy":true,"version":"1.19.3"}"""));
            }
        };
        await serve.PollAsync(CancellationToken.None);
        Assert.True(serve.Status.Healthy);
        Assert.Equal("1.19.3", serve.Status.Version);
        Assert.Equal(new[] { "/api/info", "/global/health" }, paths);
    }

    [Fact]
    public async Task PollAsync_V1_HtmlHealthBody_IsUnhealthy()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/info")
                    return Task.FromResult(StatusCode(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>spa</body></html>", System.Text.Encoding.UTF8, "text/html")
                });
            }
        };
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("JSON health body", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSession_V2_PostsApiSessionWithAuth()
    {
        string? path = null;
        string? scheme = null;
        string body = "";
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = async (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                scheme = request.Headers.Authorization?.Scheme;
                body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                return JsonResponse("""{"data":{"id":"ses_1"}}""");
            }
        };
        MarkV2(serve);
        var id = await serve.CreateSessionAsync("Chat", CancellationToken.None);
        Assert.Equal("ses_1", id);
        Assert.Equal("/api/session", path);
        Assert.Equal("Basic", scheme);
        Assert.Contains("\"title\":\"Chat\"", body);
    }

    [Fact]
    public async Task CreateSession_V1_PostsRootSessionWithoutAuth()
    {
        string? path = null;
        var hasAuth = false;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                hasAuth = request.Headers.Authorization is not null;
                return Task.FromResult(JsonResponse("""{"id":"oc_1"}"""));
            }
        };
        var id = await serve.CreateSessionAsync("Chat", CancellationToken.None);
        Assert.Equal("oc_1", id);
        Assert.Equal("/session", path);
        Assert.False(hasAuth);
    }

    [Fact]
    public async Task PromptAsync_V2_SwitchesModelThenPromptsTextOnly()
    {
        var calls = new List<(string Path, string Body)>();
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                calls.Add((request.RequestUri!.AbsolutePath, body));
                return Task.FromResult(JsonResponse("""{"data":{"id":"inb_1"}}"""));
            }
        };
        MarkV2(serve);
        await serve.PromptAsync("ses_1", "hello", "anthropic/claude-sonnet", null, CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.Equal("/api/session/ses_1/model", calls[0].Path);
        Assert.Contains("\"providerID\":\"anthropic\"", calls[0].Body);
        Assert.Contains("\"id\":\"claude-sonnet\"", calls[0].Body);
        Assert.DoesNotContain("modelID", calls[0].Body);
        Assert.Equal("/api/session/ses_1/prompt", calls[1].Path);
        Assert.Contains("\"text\":\"hello\"", calls[1].Body);
        Assert.DoesNotContain("parts", calls[1].Body);
        Assert.DoesNotContain("temperature", calls[1].Body);
    }

    [Fact]
    public async Task PromptAsync_V2_NoModel_PromptsOnce()
    {
        var paths = new List<string>();
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(JsonResponse("""{"data":{"id":"inb_1"}}"""));
            }
        };
        MarkV2(serve);
        await serve.PromptAsync("ses_1", "hello", null, null, CancellationToken.None);
        Assert.Equal(new[] { "/api/session/ses_1/prompt" }, paths);
    }

    [Fact]
    public async Task PromptAsync_V1_UsesPromptAsyncWithParts()
    {
        string? path = null;
        string body = "";
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return Task.FromResult(StatusCode(HttpStatusCode.NoContent));
            }
        };
        await serve.PromptAsync("s1", "hello", "anthropic/claude-sonnet", null, CancellationToken.None);
        Assert.Equal("/session/s1/prompt_async", path);
        Assert.Contains("\"parts\"", body);
        Assert.Contains("\"providerID\":\"anthropic\"", body);
    }

    [Fact]
    public async Task AbortAsync_V2_PostsInterruptWithoutBody()
    {
        string? path = null;
        var hasBody = false;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                hasBody = request.Content is not null;
                return Task.FromResult(JsonResponse("""{"interrupted":true}"""));
            }
        };
        MarkV2(serve);
        await serve.AbortAsync("ses_1", CancellationToken.None);
        Assert.Equal("/api/session/ses_1/interrupt", path);
        Assert.False(hasBody);
        Assert.Equal(1, serve.AbortCount);
    }

    [Fact]
    public async Task AbortAsync_V1_PostsAbort()
    {
        string? path = null;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(StatusCode(HttpStatusCode.NoContent));
            }
        };
        await serve.AbortAsync("s1", CancellationToken.None);
        Assert.Equal("/session/s1/abort", path);
    }

    [Fact]
    public async Task AbortAsync_V2WithoutPassword_DetectsViaInfoAndWaitsForPassword()
    {
        string? path = null;
        var scheme = "";
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                scheme = request.Headers.Authorization?.Scheme ?? "";
                return path == "/api/info"
                    ? Task.FromResult(StatusCode(HttpStatusCode.Unauthorized))
                    : Task.FromResult(JsonResponse("""{"interrupted":true}"""));
            }
        };
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            serve.HandleStdoutLine("SERVER PASSWORD pw123");
        });
        await serve.AbortAsync("ses_1", CancellationToken.None);
        Assert.Equal("/api/session/ses_1/interrupt", path);
        Assert.Equal("Basic", scheme);
    }

    [Fact]
    public async Task AbortAsync_V1DetectedViaInfo_UsesV1Path()
    {
        string? path = null;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                return path == "/api/info"
                    ? Task.FromResult(StatusCode(HttpStatusCode.NotFound))
                    : Task.FromResult(StatusCode(HttpStatusCode.NoContent));
            }
        };
        await serve.AbortAsync("s1", CancellationToken.None);
        Assert.Equal("/session/s1/abort", path);
    }

    [Fact]
    public async Task ListParts_ParsesV2MessageList()
    {
        string? path = null;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(JsonResponse("""
                    {
                      "data": [
                        {"id":"m1","time":{"created":1},"type":"user","text":"hi"},
                        {"id":"m2","time":{"created":2},"type":"assistant","agent":"a",
                         "model":{"providerID":"p","modelID":"m"},
                         "content":[
                           {"type":"text","text":"hello"},
                           {"type":"reasoning","text":"think"},
                           {"id":"c1","type":"tool","name":"read","state":{"status":"completed","time":{"start":1000,"end":2500},"content":[{"type":"text","text":"src/a.cs"}]}},
                           {"type":"tool","name":"bad","state":{"status":"error","error":{"message":"boom"}}}
                         ],
                         "tokens":{"input":10,"output":5},"cost":0.001}
                      ],
                      "cursor":{"previous":null,"next":null}
                    }
                    """));
            }
        };

        MarkV2(serve);
        var parts = await serve.ListPartsAsync("ses_1", CancellationToken.None);
        Assert.Equal("/api/session/ses_1/message", path);
        Assert.Equal(5, parts.Count);
        Assert.Equal("user", parts[0].Role);
        Assert.Equal("hi", parts[0].Body);
        Assert.Equal("m1", parts[0].ExternalId);
        Assert.Equal("assistant", parts[1].Role);
        Assert.Equal("text", parts[1].Kind);
        Assert.Equal("hello", parts[1].Body);
        Assert.Equal("m2:text:0", parts[1].ExternalId);
        Assert.Equal("reasoning", parts[2].Kind);
        Assert.Equal("m2:reasoning:0", parts[2].ExternalId);
        Assert.Equal("tool", parts[3].Kind);
        Assert.Equal("read\ncompleted\nsrc/a.cs", parts[3].Body);
        Assert.Equal("c1", parts[3].ExternalId);
        Assert.Null(parts[3].DurationMs);
        Assert.Equal("bad\nerror\nboom", parts[4].Body);
        Assert.Equal("m2:tool:1", parts[4].ExternalId);
    }

    [Fact]
    public async Task ListParts_V2_IgnoresNonChatMessageTypes()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                {"data":[
                  {"id":"m1","type":"system","text":"boot"},
                  {"id":"m2","type":"idle"},
                  {"id":"m3","time":{"created":1},"type":"user","text":"hi"}
                ],"cursor":{"previous":null,"next":null}}
                """))
        };
        var parts = await serve.ListPartsAsync("ses_1", CancellationToken.None);
        Assert.Single(parts);
        Assert.Equal("hi", parts[0].Body);
    }

    [Fact]
    public async Task ReadUsage_ParsesV2DataWrapper()
    {
        string? path = null;
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(JsonResponse("""
                    {"data":[
                      {"id":"m1","time":{"created":1},"type":"user","text":"hi"},
                      {"id":"m2","time":{"created":2},"type":"assistant","content":[],
                       "tokens":{"input":10,"output":5,"reasoning":2,"cache":{"read":1,"write":0}},"cost":0.001}
                    ],"cursor":{"previous":null,"next":null}}
                    """));
            }
        };
        MarkV2(serve);
        var usage = await serve.ReadUsageAsync("ses_1", CancellationToken.None);
        Assert.Equal("/api/session/ses_1/message", path);
        Assert.Equal(1, usage.AssistantMessages);
        Assert.Equal(11, usage.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);
        Assert.Equal(0.001, usage.TotalCost, 3);
    }

    [Fact]
    public async Task PollAsync_V2_401WithStalePassword_OwnProcessDead_SaysNotRunning()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(StatusCode(HttpStatusCode.Unauthorized))
        };
        MarkV2(serve);
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("not running", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rejected credentials", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TickAsync_PortBusy_SkipsLaunchAndReportsHolder()
    {
        await using var serve = new ClientOpenCodeServe
        {
            PortHolderProbe = _ => 4242,
            BinaryProbe = _ => "opencode"
        };
        await serve.TickAsync(_dir, CancellationToken.None);
        Assert.Equal(0, serve.StartCount);
        Assert.False(serve.Status.Healthy);
        Assert.Contains("4242", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in use", serve.Status.Error, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Poll_FreshServe_401Then200_RetriesUntilHealthy()
    {
        var calls = 0;
        await using var serve = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (_, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromResult(StatusCode(HttpStatusCode.Unauthorized))
                    : Task.FromResult(JsonResponse("""{"version":"2.1.0","pid":7}"""));
            }
        };
        MarkV2(serve);
        serve.TestSetStartedAt(DateTime.UtcNow);
        await serve.PollAsync(CancellationToken.None);
        Assert.True(serve.Status.Healthy);
        Assert.Equal("2.1.0", serve.Status.Version);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Poll_StaleServe_401_DoesNotRetry()
    {
        var calls = 0;
        await using var serve = new ClientOpenCodeServe
        {
            TestForceRunning = true,
            SendAsync = (_, _) =>
            {
                calls++;
                return Task.FromResult(StatusCode(HttpStatusCode.Unauthorized));
            }
        };
        MarkV2(serve);
        serve.TestSetStartedAt(DateTime.UtcNow.AddSeconds(-60));
        await serve.PollAsync(CancellationToken.None);
        Assert.False(serve.Status.Healthy);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task GetTurnStatus_V2_ToolCallsIsNotEnded()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"tool-calls","content":[{"type":"reasoning","id":"p1","text":"r","time":{"created":1,"completed":2}}]}]
                """))
        };
        MarkV2(serve);
        var turn = await serve.GetTurnStatusAsync("ses_1", CancellationToken.None);
        Assert.NotNull(turn);
        Assert.False(turn!.Ended);
        Assert.Equal("tool-calls", turn.Finish);
    }

    [Fact]
    public async Task GetTurnStatus_V2_StopIsEnded()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"stop","content":[{"type":"text","id":"p1","text":"done","time":{"created":1,"completed":2}}]}]
                """))
        };
        MarkV2(serve);
        var turn = await serve.GetTurnStatusAsync("ses_1", CancellationToken.None);
        Assert.NotNull(turn);
        Assert.True(turn!.Ended);
        Assert.Equal("stop", turn.Finish);
        Assert.Null(turn.Error);
    }

    [Fact]
    public async Task GetTurnStatus_V2_ErrorIsEnded()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"type":"message","role":"assistant","id":"m1","time":{"created":1},"finish":"error","error":{"type":"provider.invalid-request","message":"context overflow"},"content":[]}]
                """))
        };
        MarkV2(serve);
        var turn = await serve.GetTurnStatusAsync("ses_1", CancellationToken.None);
        Assert.NotNull(turn);
        Assert.True(turn!.Ended);
        Assert.Equal("context overflow", turn.Error);
    }

    [Fact]
    public async Task GetTurnStatus_NoAssistantMessage_ReturnsNull()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"role":"user","text":"hi"}]
                """))
        };
        MarkV2(serve);
        Assert.Null(await serve.GetTurnStatusAsync("ses_1", CancellationToken.None));
    }

    [Fact]
    public async Task GetTurnStatus_V1_WrappedInfo()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"info":{"id":"m1","role":"assistant","time":{"created":1,"completed":2},"finish":"stop"},"parts":[]}]
                """))
        };
        var turn = await serve.GetTurnStatusAsync("ses_1", CancellationToken.None);
        Assert.NotNull(turn);
        Assert.True(turn!.Ended);
        Assert.Equal("stop", turn.Finish);
    }

    [Fact]
    public async Task GetTurnStatus_V2_ReturnsNewestAssistantMessageId()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => Task.FromResult(JsonResponse("""
                [{"type":"message","role":"assistant","id":"m2","time":{"created":3},"content":[]},{"type":"message","role":"assistant","id":"m1","time":{"created":1,"completed":2},"finish":"stop","content":[]}]
                """))
        };
        MarkV2(serve);
        var turn = await serve.GetTurnStatusAsync("ses_1", CancellationToken.None);
        Assert.NotNull(turn);
        Assert.False(turn!.Ended);
        Assert.Equal("m2", turn.MessageId);
    }

    [Fact]
    public async Task StreamSessionText_V2_DeliversDeltasAndSkipsForeignFrames()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.EndsWith("/api/event", request.RequestUri!.AbsolutePath);
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                var body = """
                    : heartbeat

                    data: {"type":"message.updated","data":{"id":"m1"}}
                    data: {"type":"session.text.delta","data":{"sessionID":"other","assistantMessageID":"m9","ordinal":0,"delta":"nope"}}
                    data: {"type":"session.text.delta","data":{"sessionID":"ses_1","assistantMessageID":"m1","ordinal":0,"delta":"Hel"}}
                    data: {"type":"session.reasoning.delta","data":{"sessionID":"ses_1","assistantMessageID":"m1","ordinal":2,"delta":"think"}}
                    data: not-json
                    data: {"type":"session.text.delta","data":{"sessionID":"ses_1","assistantMessageID":"m1","ordinal":1,"delta":"lo"}}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                });
            }
        };
        MarkV2(serve);

        var deltas = new List<(string Kind, string MessageId, int Ordinal, string Delta)>();
        await serve.StreamSessionTextAsync("ses_1", (kind, messageId, ordinal, delta, _) =>
        {
            deltas.Add((kind, messageId, ordinal, delta));
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(
            new[] { ("text", "m1", 0, "Hel"), ("reasoning", "m1", 2, "think"), ("text", "m1", 1, "lo") },
            deltas);
    }

    [Fact]
    public async Task StreamSessionText_V1_IsNoOp()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) => throw new InvalidOperationException("v1 has no event feed")
        };
        var called = false;
        await serve.StreamSessionTextAsync("ses_1", (k, m, o, d, ct) =>
        {
            called = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.False(called);
    }

    [Fact]
    public async Task StreamSessionText_CancellationStopsOpenStream()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (_, _) =>
            {
                var frame = """
                    data: {"type":"session.text.delta","data":{"sessionID":"ses_1","assistantMessageID":"m1","ordinal":0,"delta":"Hel"}}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingStream(frame.TrimEnd('\r', '\n') + "\n"))
                });
            }
        };
        MarkV2(serve);

        var received = new List<string>();
        var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var streamTask = serve.StreamSessionTextAsync("ses_1", (k, m, o, d, ct) =>
        {
            received.Add(d);
            firstFrame.TrySetResult(true);
            return Task.CompletedTask;
        }, cts.Token);

        // The first frame must be delivered before the turn token is cancelled.
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The stream then hangs on the next read; the cancellation must end the task.
        cts.Cancel();
        var ended = await Task.WhenAny(streamTask, Task.Delay(10_000));
        Assert.Same(streamTask, ended);
        Assert.Equal(new[] { "Hel" }, received);
    }

    [Fact]
    public async Task ReadContextLimit_V2_ExactMatchOrUnknown()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.Equal("/api/model", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [{"id":"flint","modelID":"flint-40b-free","providerID":"opencode","limit":{"context":262144,"output":65536}},
                         {"id":"other","modelID":"nemotron","providerID":"opencode","limit":{"context":128000}}]
                        """, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };
        MarkV2(serve);
        Assert.Equal(262144, await serve.ReadContextLimitAsync("opencode/flint-40b-free", CancellationToken.None));
        Assert.Equal(128000, await serve.ReadContextLimitAsync("opencode/nemotron", CancellationToken.None));
        // No fallback: an unknown or missing model leaves the limit unknown.
        Assert.Null(await serve.ReadContextLimitAsync(null, CancellationToken.None));
        Assert.Null(await serve.ReadContextLimitAsync("", CancellationToken.None));
        Assert.Null(await serve.ReadContextLimitAsync("opencode/missing", CancellationToken.None));
    }

    [Fact]
    public async Task SwitchAgent_V2_PostsAgentAndAccepts204()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/session/ses_1/agent", request.RequestUri!.AbsolutePath);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"agent\":\"plan\"", body);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        };
        MarkV2(serve);
        await serve.SwitchAgentAsync("ses_1", "plan", CancellationToken.None);
    }

    [Fact]
    public async Task SwitchAgent_V2_UnknownAgentThrows()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) => Task.FromResult(StatusCode(HttpStatusCode.NotFound)),
        };
        MarkV2(serve);
        await Assert.ThrowsAsync<HttpRequestException>(() => serve.SwitchAgentAsync("ses_1", "ghost", CancellationToken.None));
    }

    [Fact]
    public async Task SwitchAgent_V1_Throws()
    {
        await using var serve = new ClientOpenCodeServe { SkipRealProcess = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => serve.SwitchAgentAsync("ses_1", "plan", CancellationToken.None));
    }

    [Fact]
    public async Task ListPendingForms_V2_ParsesEnvelope()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.Equal("/api/session/ses_1/form", request.RequestUri!.AbsolutePath);
                return Task.FromResult(JsonResponse("""
                    {"data":[{"id":"f1","title":"Pick an option","metadata":"{}","fields":[{"key":"choice","type":"string","required":true}]}]}
                    """));
            }
        };
        MarkV2(serve);
        var forms = await serve.ListPendingFormsAsync("ses_1", CancellationToken.None);
        Assert.Single(forms);
        Assert.Equal("f1", forms[0].Id);
        Assert.Equal("Pick an option", forms[0].Title);
        Assert.Contains("\"choice\"", forms[0].FieldsJson);
    }

    [Fact]
    public async Task ListPendingForms_V2_EmptyWhenNone()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) => Task.FromResult(JsonResponse("{\"data\":[]}"))
        };
        MarkV2(serve);
        Assert.Empty(await serve.ListPendingFormsAsync("ses_1", CancellationToken.None));
    }

    [Fact]
    public async Task ReplyForm_V2_SendsAnswerObject()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/session/ses_1/form/f1/reply", request.RequestUri!.AbsolutePath);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"answer\":{\"choice\":\"b\"}", body);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        };
        MarkV2(serve);
        Assert.True(await serve.ReplyFormAsync("ses_1", "f1", "{\"choice\":\"b\"}", CancellationToken.None));
    }

    [Fact]
    public async Task ReplyForm_V2_AlreadySettledReturnsFalse()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) => Task.FromResult(StatusCode(HttpStatusCode.Conflict)),
        };
        MarkV2(serve);
        Assert.False(await serve.ReplyFormAsync("ses_1", "f1", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task ReadContextLimit_V1_ReadsAppModels()
    {
        await using var serve = new ClientOpenCodeServe
        {
            SendAsync = (request, _) =>
            {
                Assert.Equal("/app/models", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [{"providerID":"local","modelID":"qwen","limit":{"context":32768}}]
                        """, System.Text.Encoding.UTF8, "application/json")
                });
            }
        };
        Assert.Equal(32768, await serve.ReadContextLimitAsync("local/qwen", CancellationToken.None));
        Assert.Null(await serve.ReadContextLimitAsync("local/missing", CancellationToken.None));
    }

    /// <summary>Emits one frame, then blocks on read until the cancellation token fires.</summary>
    private sealed class HangingStream : Stream
    {
        private readonly MemoryStream _head = new();
        private bool _headDone;

        public HangingStream(string frame)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(frame);
            _head.Write(bytes, 0, bytes.Length);
            _head.Position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_headDone)
            {
                _headDone = true;
                return Task.FromResult(_head.Read(buffer, offset, count));
            }
            if (cancellationToken.IsCancellationRequested)
                return Task.FromException<int>(new OperationCanceledException(cancellationToken));
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The registration must outlive this call: it fires when the turn token is cancelled.
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }
}
