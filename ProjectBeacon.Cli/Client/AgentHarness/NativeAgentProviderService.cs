namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Resolves configured model providers and creates a short-lived provider session for one turn.
/// It centralizes local-model scheduling and provider-specific construction.
/// </summary>
internal sealed class NativeAgentProviderService(
    HttpClient controlPlane,
    ClientLlamaSwap llama,
    IDaemonLlamaSync llamaSync,
    Func<WorkstationSettings> loadSettings,
    Func<HttpClient, string, string?, IChatProvider> providerFactory)
{
    private readonly HttpClient _controlPlane = controlPlane;
    private readonly ClientLlamaSwap _llama = llama;
    private readonly IDaemonLlamaSync _llamaSync = llamaSync;
    private readonly Func<WorkstationSettings> _loadSettings = loadSettings;
    private readonly Func<HttpClient, string, string?, IChatProvider> _providerFactory = providerFactory;

    public async Task<NativeAgentProviderSession> OpenSessionAsync(string model, CancellationToken ct)
    {
        var resolved = await ResolveAsync(model, ct);
        return new NativeAgentProviderSession(resolved, _llama, _llamaSync, _providerFactory);
    }

    private async Task<NativeResolvedProvider> ResolveAsync(string model, CancellationToken ct)
    {
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1)
            throw new InvalidOperationException("Model id must use provider/model format.");

        var providerId = model[..separator].Trim();
        var modelId = model[(separator + 1)..].Trim();
        if (providerId.Equals("beacon-local", StringComparison.OrdinalIgnoreCase))
        {
            var port = _llama.Port > 0 ? _llama.Port : _loadSettings().LlamaSwapPort;
            return new NativeResolvedProvider(
                "beacon-local",
                $"http://127.0.0.1:{port}/v1",
                null,
                modelId,
                true);
        }

        using var response = await _controlPlane.GetAsync("/v1/devices/me/provider-connections", ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not load provider connections (HTTP {(int)response.StatusCode}).");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("value", out var wrapped))
        {
            root = wrapped;
        }

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Provider connection response is not a list.");

        foreach (var row in root.EnumerateArray())
        {
            var id = ReadString(row, "providerId");
            if (!string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase))
                continue;

            var baseUrl = ReadString(row, "baseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException($"Provider '{providerId}' has no base URL.");

            var apiKey = ReadString(row, "apiKey");
            return new NativeResolvedProvider(providerId, baseUrl.TrimEnd('/'), apiKey, modelId, false);
        }

        throw new InvalidOperationException($"No connection is configured for provider '{providerId}'.");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>A single provider's resource scope and execution policy for one turn.</summary>
internal sealed class NativeAgentProviderSession : IAsyncDisposable
{
    private readonly NativeResolvedProvider _resolved;
    private readonly ClientLlamaSwap _llama;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly Func<HttpClient, string, string?, IChatProvider> _providerFactory;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    internal NativeAgentProviderSession(
        NativeResolvedProvider resolved,
        ClientLlamaSwap llama,
        IDaemonLlamaSync llamaSync,
        Func<HttpClient, string, string?, IChatProvider> providerFactory)
    {
        _resolved = resolved;
        _llama = llama;
        _llamaSync = llamaSync;
        _providerFactory = providerFactory;
    }

    public string Model => _resolved.Model;

    public IChatProvider CreateProvider()
    {
        var baseUrl = _resolved.IsLocal
            ? _llama.GetLocalModelBaseUrl(_resolved.Model)
            : _resolved.BaseUrl;
        return string.Equals(_resolved.ProviderId, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicProvider(_http, baseUrl, _resolved.ApiKey)
            : _providerFactory(_http, baseUrl, _resolved.ApiKey);
    }

    public async Task RunModelRequestAsync(Func<Task> request, CancellationToken requestCt)
    {
        if (!_resolved.IsLocal)
        {
            await request();
            return;
        }

        await _llamaSync.RunModelRequestAsync(
            () => _llama.IsModelConcurrent(_resolved.Model),
            async () =>
            {
                // Read residency after acquiring the lifecycle lease. Exclusive swap-group
                // models may only be selected once they own the shared model slot.
                if (_llama.UseOwnSwapper && !_llama.IsModelConcurrent(_resolved.Model))
                    await _llama.EnsureSwapModelAsync(_resolved.Model, requestCt);
                await request();
            },
            requestCt);
    }

    public async Task<string> SummarizeAsync(ChatProviderRequest summaryRequest, CancellationToken requestCt)
    {
        var summary = new StringBuilder();
        await RunModelRequestAsync(async () =>
        {
            var summaryProvider = CreateProvider();
            await foreach (var item in summaryProvider.StreamAsync(summaryRequest, requestCt))
            {
                if (item.Kind == ChatProviderEventKind.TextDelta && item.Text is not null)
                    summary.Append(item.Text);
            }
        }, requestCt);
        return summary.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
