namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class OpenAiCompatProviderTests
{
    [Fact]
    public async Task StreamsReasoningTextToolFragmentsUsageAndFinish()
    {
        const string stream = """
            data: {"choices":[{"delta":{"reasoning_content":"checking"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"Done."},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read_file","arguments":"{\"path\":\"src/"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"a.cs\"}"}}]},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_tokens":41,"completion_tokens":9}}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
        });
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatProvider(http, "http://127.0.0.1:8080/v1/", "test-secret");
        var request = new ChatProviderRequest(
            "test-model",
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "test" } },
            new JsonArray { new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file" } } },
            ReasoningEffort: "medium");

        var events = new List<ChatProviderEvent>();
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
            events.Add(item);

        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.ReasoningDelta && e.Text == "checking");
        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.TextDelta && e.Text == "Done.");
        Assert.Equal(2, events.Count(e => e.Kind == ChatProviderEventKind.ToolCallDelta));
        Assert.Equal("read_file", events.First(e => e.Kind == ChatProviderEventKind.ToolCallDelta && e.ToolName is not null).ToolName);
        Assert.Equal("{\"path\":\"src/", events.First(e => e.Kind == ChatProviderEventKind.ToolCallDelta).ToolArguments);
        Assert.Equal("a.cs\"}", events.Last(e => e.Kind == ChatProviderEventKind.ToolCallDelta).ToolArguments);
        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.Usage && e.PromptTokens == 41 && e.CompletionTokens == 9);
        Assert.Equal("tool_calls", events.First(e => e.Kind == ChatProviderEventKind.Finished).FinishReason);

        Assert.Equal("Bearer test-secret", handler.LastAuthorization);
        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Equal("test-model", body!["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("medium", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreservesMultipleToolIndexesAndUnicodeArgumentFragments()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-a","function":{"name":"read_file","arguments":"{\\"path\\":\\"src/"}} ,{"index":1,"id":"call-b","function":{"name":"read_file","arguments":"{\\"path\\":\\"данные/"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"alpha.cs\\"}"}} ,{"index":1,"function":{"arguments":"файл.cs\\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
        }));
        var provider = new OpenAiCompatProvider(http, "http://127.0.0.1:8080/v1", null);
        var events = new List<ChatProviderEvent>();
        await foreach (var item in provider.StreamAsync(new ChatProviderRequest("model", new JsonArray(), new JsonArray()), CancellationToken.None))
            events.Add(item);

        var calls = events.Where(item => item.Kind == ChatProviderEventKind.ToolCallDelta).ToArray();
        Assert.Contains(calls, item => item.ToolIndex == 0 && item.ToolCallId == "call-a");
        Assert.Contains(calls, item => item.ToolIndex == 1 && item.ToolCallId == "call-b");
        Assert.Equal("{\\"path\\":\\"src/", string.Concat(calls.Where(item => item.ToolIndex == 0).Select(item => item.ToolArguments)));
        Assert.Equal("{\\"path\\":\\"данные/файл.cs\\"}", string.Concat(calls.Where(item => item.ToolIndex == 1).Select(item => item.ToolArguments)));
    }

    [Fact]
    public async Task RejectsStreamThatEndsAfterToolCallFinishWithoutDoneSentinel()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"read_file","arguments":"{\"path\":\"README.md\"}"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            """;
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
        }));
        var provider = new OpenAiCompatProvider(http, "http://127.0.0.1:8080/v1", null);
        var request = new ChatProviderRequest("test-model", new JsonArray(), new JsonArray());

        var exception = await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }
        });

        Assert.True(exception.Retryable);
        Assert.Contains("before [DONE]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNonHttpUrlBeforeSending()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("request should not run")));
        Assert.Throws<ArgumentException>(() => new OpenAiCompatProvider(http, "file:///tmp/model", null));
    }

    [Fact]
    public async Task DoesNotIncludeProviderResponseBodyInHttpError()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("sensitive gateway response")
        }));
        var provider = new OpenAiCompatProvider(http, "https://example.test/v1", "private-key");
        var request = new ChatProviderRequest("model", new JsonArray(), new JsonArray());

        var ex = await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }
        });
        Assert.True(ex.Retryable);
        Assert.DoesNotContain("sensitive gateway response", ex.Message);
        Assert.DoesNotContain("private-key", ex.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
