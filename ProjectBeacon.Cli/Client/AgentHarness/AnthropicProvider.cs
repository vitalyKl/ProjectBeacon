namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Anthropic Messages API adapter for the same internal event/tool-call contract used by the
/// OpenAI-compatible provider. Keys remain in memory and errors never echo provider response bodies.
/// </summary>
internal sealed class AnthropicProvider(HttpClient http, string baseUrl, string? apiKey) : IChatProvider
{
    private readonly Uri _endpoint = ResolveEndpoint(baseUrl);

    public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
        ChatProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var maxTokens = request.MaxTokens is > 0 ? request.MaxTokens.Value : 8192;
        var thinkingBudget = GetThinkingBudget(request.ReasoningEffort);
        if (thinkingBudget is { } budget && maxTokens <= budget)
            maxTokens = budget + 1024;

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["messages"] = ConvertMessages(request.Messages)
        };

        var system = ConvertSystemMessages(request.Messages);
        if (!string.IsNullOrWhiteSpace(system))
            body["system"] = system;

        if (request.Tools.Count > 0)
        {
            body["tools"] = ConvertTools(request.Tools);
            body["tool_choice"] = new JsonObject { ["type"] = "auto" };
        }

        // Anthropic extended thinking does not accept temperature on the same request.
        if (thinkingBudget is { } thinking)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = thinking
            };
        }
        else if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.RequestTimeout
                || status >= 500
                || status == 529;
            throw new ChatProviderException($"Anthropic provider returned HTTP {status}.", status, retryable);
        }

        int? inputTokens = null;
        int? outputTokens = null;
        string? finishReason = null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (data.Length == 0)
                    continue;
                var payload = data.ToString();
                data.Clear();
                using var doc = ParsePayload(payload);
                var root = doc.RootElement;
                var type = ReadString(root, "type");

                if (type == "error")
                {
                    var errorType = root.TryGetProperty("error", out var error)
                        ? ReadString(error, "type") ?? "provider_error"
                        : "provider_error";
                    var retryable = errorType is "overloaded_error" or "rate_limit_error" or "api_error";
                    throw new ChatProviderException($"Anthropic stream returned {errorType}.", retryable ? 503 : 400, retryable);
                }

                switch (type)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var started)
                            && started.TryGetProperty("usage", out var startedUsage))
                        {
                            inputTokens = ReadInt(startedUsage, "input_tokens") ?? inputTokens;
                            outputTokens = ReadInt(startedUsage, "output_tokens") ?? outputTokens;
                        }
                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var block)
                            && ReadString(block, "type") == "tool_use")
                        {
                            yield return new ChatProviderEvent(
                                ChatProviderEventKind.ToolCallDelta,
                                ToolIndex: ReadInt(root, "index") ?? 0,
                                ToolCallId: ReadString(block, "id"),
                                ToolName: ReadString(block, "name"));
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            switch (ReadString(delta, "type"))
                            {
                                case "text_delta":
                                    if (ReadString(delta, "text") is { Length: > 0 } text)
                                        yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: text);
                                    break;
                                case "thinking_delta":
                                    if (ReadString(delta, "thinking") is { Length: > 0 } thinkingText)
                                        yield return new ChatProviderEvent(ChatProviderEventKind.ReasoningDelta, Text: thinkingText);
                                    break;
                                case "input_json_delta":
                                    if (ReadString(delta, "partial_json") is { Length: > 0 } partial)
                                        yield return new ChatProviderEvent(
                                            ChatProviderEventKind.ToolCallDelta,
                                            ToolIndex: ReadInt(root, "index") ?? 0,
                                            ToolArguments: partial);
                                    break;
                            }
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var messageDelta))
                        {
                            var rawStop = ReadString(messageDelta, "stop_reason");
                            if (rawStop is not null)
                                finishReason = MapStopReason(rawStop);
                        }
                        if (root.TryGetProperty("usage", out var usage))
                        {
                            inputTokens = ReadInt(usage, "input_tokens") ?? inputTokens;
                            outputTokens = ReadInt(usage, "output_tokens") ?? outputTokens;
                        }
                        if (finishReason is not null)
                            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: finishReason);
                        break;

                    case "message_stop":
                        // Anthropic usage fields are cumulative across stream events; emit one final
                        // usage item instead of summing repeated cumulative snapshots.
                        yield return new ChatProviderEvent(
                            ChatProviderEventKind.Usage,
                            PromptTokens: inputTokens,
                            CompletionTokens: outputTokens);
                        yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
                        yield break;
                }

                continue;
            }

            if (line.StartsWith(':') || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var value = line.AsSpan(5).TrimStart();
            if (data.Length > 0)
                data.Append('\n');
            data.Append(value);
        }

        // A clean message_stop is required. A truncated stream must not execute incomplete tools.
        if (data.Length > 0)
        {
            using var doc = ParsePayload(data.ToString());
            if (ReadString(doc.RootElement, "type") == "message_stop")
            {
                yield return new ChatProviderEvent(ChatProviderEventKind.Usage, PromptTokens: inputTokens, CompletionTokens: outputTokens);
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
                yield break;
            }
        }
        throw new IOException("Anthropic stream ended before message_stop.");
    }

    private static JsonDocument ParsePayload(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Anthropic provider returned malformed streaming JSON.", ex);
        }
    }

    private static Uri ResolveEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A valid HTTP(S) Anthropic base URL is required.", nameof(baseUrl));

        var trimmed = baseUri.AbsoluteUri.TrimEnd('/');
        var endpoint = trimmed.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? trimmed + "/messages"
                : trimmed + "/v1/messages";
        return new Uri(endpoint, UriKind.Absolute);
    }

    private static JsonArray ConvertMessages(JsonArray messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            if (message is not JsonObject obj)
                continue;
            var role = ReadNodeString(obj["role"]);
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var block = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = ReadNodeString(obj["tool_call_id"]) ?? "missing-tool-id",
                    ["content"] = ReadNodeString(obj["content"]) ?? ""
                };
                AppendUserBlock(result, block);
                continue;
            }

            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                && obj["tool_calls"] is JsonArray toolCalls)
            {
                var blocks = new JsonArray();
                if (ReadNodeString(obj["content"]) is { Length: > 0 } text)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });

                foreach (var toolCall in toolCalls.OfType<JsonObject>())
                {
                    var function = toolCall["function"] as JsonObject;
                    var rawArgs = ReadNodeString(function?["arguments"]);
                    JsonObject input;
                    try { input = JsonNode.Parse(string.IsNullOrWhiteSpace(rawArgs) ? "{}" : rawArgs) as JsonObject ?? new JsonObject(); }
                    catch (JsonException) { input = new JsonObject(); }
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = ReadNodeString(toolCall["id"]) ?? "missing-tool-id",
                        ["name"] = ReadNodeString(function?["name"]) ?? "unknown_tool",
                        ["input"] = input
                    });
                }

                result.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = blocks
                });
                continue;
            }

            var content = obj["content"];
            result.Add(new JsonObject
            {
                ["role"] = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                ["content"] = content?.DeepClone() ?? JsonValue.Create("")
            });
        }

        return result;
    }

    private static void AppendUserBlock(JsonArray messages, JsonObject block)
    {
        if (messages.LastOrDefault() is JsonObject last
            && string.Equals(ReadNodeString(last["role"]), "user", StringComparison.OrdinalIgnoreCase))
        {
            if (last["content"] is JsonArray blocks)
            {
                blocks.Add(block);
                return;
            }

            var existing = last["content"]?.DeepClone();
            var converted = new JsonArray();
            if (ReadNodeString(existing) is { Length: > 0 } text)
                converted.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            converted.Add(block);
            last["content"] = converted;
            return;
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(block)
        });
    }

    private static string ConvertSystemMessages(JsonArray messages) =>
        string.Join("\n\n", messages.OfType<JsonObject>()
            .Where(m => string.Equals(ReadNodeString(m["role"]), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => ReadNodeString(m["content"]))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static JsonArray ConvertTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var function = tool["function"] as JsonObject;
            var name = ReadNodeString(function?["name"]);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = ReadNodeString(function?["description"]) ?? "",
                ["input_schema"] = function?["parameters"]?.DeepClone() ?? new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            });
        }
        return result;
    }

    private static int? GetThinkingBudget(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            "low" => 1024,
            "medium" => 4096,
            "high" => 10000,
            "max" or "maximum" => 16000,
            _ => null
        };

    private static string MapStopReason(string raw) => raw switch
    {
        "tool_use" => "tool_calls",
        "end_turn" => "stop",
        "max_tokens" => "length",
        _ => raw
    };

    private static string? ReadNodeString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
