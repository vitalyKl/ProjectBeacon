namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task StreamsThinkingTextToolInputUsageAndStopReason()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":41,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Checking facts."}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"src/"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"README.md\"}"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":41,"output_tokens":9}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
        });
        using var http = new HttpClient(handler);
        var provider = new AnthropicProvider(http, "https://api.anthropic.test/v1", "test-secret");
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read files",
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                }
            }
        };
        var request = new ChatProviderRequest(
            "claude-test",
            new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are helpful." },
                new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "toolu_old",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"old.txt\"}" }
                    }
                } },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "toolu_old", ["content"] = "old file contents" },
                new JsonObject { ["role"] = "user", ["content"] = "now read another file" }
            },
            tools,
            Temperature: 0.2,
            ReasoningEffort: "medium");

        var events = new List<ChatProviderEvent>();
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
            events.Add(item);

        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.ReasoningDelta && e.Text == "Checking facts.");
        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.ToolCallDelta && e.ToolCallId == "toolu_1" && e.ToolName == "read_file");
        Assert.Equal(2, events.Count(e => e.Kind == ChatProviderEventKind.ToolCallDelta && e.ToolArguments is not null));
        Assert.Equal("{\"path\":\"src/", events.First(e => e.Kind == ChatProviderEventKind.ToolCallDelta && e.ToolArguments is not null).ToolArguments);
        Assert.Equal("README.md\"}", events.Last(e => e.Kind == ChatProviderEventKind.ToolCallDelta && e.ToolArguments is not null).ToolArguments);
        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.Usage && e.PromptTokens == 41 && e.CompletionTokens == 9);
        Assert.Contains(events, e => e.Kind == ChatProviderEventKind.Finished && e.FinishReason == "tool_calls");
        Assert.Equal("test-secret", handler.LastApiKey);
        Assert.Null(handler.LastAuthorization);
        Assert.Equal("2023-06-01", handler.LastAnthropicVersion);
        var body = JsonNode.Parse(handler.LastBody!);
        Assert.Equal("You are helpful.", body!["system"]!.GetValue<string>());
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("temperature"));
        Assert.Equal("read_file", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Contains(body["messages"]!.AsArray(), message =>
            message?["role"]?.GetValue<string>() == "user"
            && message["content"] is JsonArray blocks
            && blocks.Any(block => block?["type"]?.GetValue<string>() == "tool_result"
                && block["tool_use_id"]?.GetValue<string>() == "toolu_old"));
    }

    [Fact]
    public async Task DoesNotEchoAnthropicErrorBody()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)529)
        {
            Content = new StringContent("sensitive key echo")
        }));
        var provider = new AnthropicProvider(http, "https://api.anthropic.test", "private-key");
        var request = new ChatProviderRequest("claude-test", new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } }, new JsonArray());

        var ex = await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }
        });
        Assert.True(ex.Retryable);
        Assert.DoesNotContain("sensitive key echo", ex.Message);
        Assert.DoesNotContain("private-key", ex.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }
        public string? LastApiKey { get; private set; }
        public string? LastAnthropicVersion { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.FirstOrDefault() : null;
            LastAnthropicVersion = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.FirstOrDefault() : null;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
