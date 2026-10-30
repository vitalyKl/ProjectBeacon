namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal enum ChatProviderEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCallDelta,
    Usage,
    Finished
}

internal sealed record ChatProviderEvent(
    ChatProviderEventKind Kind,
    string? Text = null,
    int? ToolIndex = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    string? FinishReason = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);

internal sealed record ChatProviderRequest(
    string Model,
    JsonArray Messages,
    JsonArray Tools,
    double? Temperature = null,
    string? ReasoningEffort = null,
    int? MaxTokens = null);

internal interface IChatProvider
{
    IAsyncEnumerable<ChatProviderEvent> StreamAsync(ChatProviderRequest request, CancellationToken ct);
}

/// <summary>
/// Streaming client for OpenAI-compatible chat-completions endpoints. Credentials are attached
/// to each request and are never written to files or included in diagnostics.
/// </summary>
internal sealed class OpenAiCompatProvider(HttpClient http, string baseUrl, string? apiKey) : IChatProvider
{
    private readonly Uri _endpoint = ResolveEndpoint(baseUrl);

    public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
        ChatProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.DeepClone(),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (request.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)
            && !string.Equals(request.ReasoningEffort, "default", StringComparison.OrdinalIgnoreCase))
            body["reasoning_effort"] = request.ReasoningEffort;
        if (request.MaxTokens is > 0)
            body["max_tokens"] = request.MaxTokens.Value;

        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                || status >= 500
                || response.StatusCode == HttpStatusCode.RequestTimeout;
            // Do not return arbitrary provider bodies: gateways sometimes echo request headers or credentials.
            throw new ChatProviderException(
                $"Chat provider returned HTTP {status}.",
                (int)response.StatusCode,
                retryable);
        }

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
                await foreach (var item in ParseEventAsync(payload, ct))
                {
                    yield return item;
                    if (item.Kind == ChatProviderEventKind.Finished && item.FinishReason == "done")
                        yield break;
                }
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var value = line.AsSpan(5).TrimStart();
            if (value.SequenceEqual("[DONE]".AsSpan()))
            {
                if (data.Length > 0)
                {
                    await foreach (var item in ParseEventAsync(data.ToString(), ct))
                        yield return item;
                    data.Clear();
                }
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
                yield break;
            }

            if (data.Length > 0)
                data.Append('\n');
            data.Append(value);
        }

        if (data.Length > 0)
        {
            await foreach (var item in ParseEventAsync(data.ToString(), ct))
                yield return item;
        }

        // A finish_reason is not sufficient proof that the HTTP stream completed: a dropped
        // connection can occur after the final delta but before the protocol's [DONE] sentinel.
        throw new ChatProviderException("OpenAI-compatible stream ended before [DONE].", (int)HttpStatusCode.BadGateway, retryable: true);
    }

    private static async IAsyncEnumerable<ChatProviderEvent> ParseEventAsync(
        string payload,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Chat provider returned malformed SSE JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var prompt = ReadInt(usage, "prompt_tokens") ?? ReadInt(usage, "input_tokens");
                var completion = ReadInt(usage, "completion_tokens") ?? ReadInt(usage, "output_tokens");
                yield return new ChatProviderEvent(
                    ChatProviderEventKind.Usage,
                    PromptTokens: prompt,
                    CompletionTokens: completion);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var choice in choices.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var reasoning = ReadString(delta, "reasoning_content")
                        ?? ReadString(delta, "reasoning")
                        ?? ReadString(delta, "analysis");
                    if (!string.IsNullOrEmpty(reasoning))
                        yield return new ChatProviderEvent(ChatProviderEventKind.ReasoningDelta, Text: reasoning);

                    var content = ReadText(delta, "content");
                    if (!string.IsNullOrEmpty(content))
                        yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: content);

                    if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tool in tools.EnumerateArray())
                        {
                            var index = ReadInt(tool, "index") ?? 0;
                            var id = ReadString(tool, "id");
                            string? name = null;
                            string? arguments = null;
                            if (tool.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                            {
                                name = ReadString(function, "name");
                                arguments = ReadString(function, "arguments");
                            }
                            if (id is not null || name is not null || arguments is not null)
                            {
                                yield return new ChatProviderEvent(
                                    ChatProviderEventKind.ToolCallDelta,
                                    ToolIndex: index,
                                    ToolCallId: id,
                                    ToolName: name,
                                    ToolArguments: arguments);
                            }
                        }
                    }
                }

                var finish = ReadString(choice, "finish_reason");
                if (finish is not null)
                    yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: finish);
            }
        }

        await Task.CompletedTask;
    }

    private static Uri ResolveEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A valid HTTP(S) chat provider base URL is required.", nameof(baseUrl));

        var trimmed = baseUri.AbsoluteUri.TrimEnd('/');
        var endpoint = trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
        return new Uri(endpoint, UriKind.Absolute);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Concat(value.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object)
                .Select(part => ReadString(part, "text") ?? string.Empty)),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}

internal sealed class ChatProviderException(string message, int statusCode, bool retryable, Exception? inner = null)
    : HttpRequestException(message, inner, (HttpStatusCode)statusCode)
{
    public bool Retryable { get; } = retryable;
}
