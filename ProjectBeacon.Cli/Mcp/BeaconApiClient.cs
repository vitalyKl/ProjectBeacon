namespace ProjectBeacon.Cli.Mcp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client;

internal sealed class BeaconApiClient
{
    private readonly HttpClient _http;
    private readonly McpEnvironment _env;

    public BeaconApiClient(HttpClient http, McpEnvironment? env = null)
    {
        _http = http;
        _env = env ?? McpEnvironment.Process;
    }

    internal readonly record struct Resolution(BeaconApiClient? Client, bool Misconfigured);

    public static BeaconApiClient? FromEnvironment() => Resolve().Client;

    internal static Resolution Resolve(McpEnvironment? env = null)
    {
        var lookup = env ?? McpEnvironment.Process;
        var settings = ClientConfiguration.ResolveApi(lookup.Get);
        if (settings.Misconfigured)
            return new Resolution(null, true);
        if (!settings.Configured)
            return new Resolution(null, false);

        var http = new HttpClient { BaseAddress = settings.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        return new Resolution(new BeaconApiClient(http, lookup), false);
    }

    public async Task<(bool Ok, int Status, string Body)> SendAsync(
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken ct = default,
        int? ifMatchRevision = null)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        var project = _env.Get("BEACON_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.TryAddWithoutValidation("X-Project-Id", project.Trim());
        if (ifMatchRevision is { } revision)
            request.Headers.TryAddWithoutValidation("If-Match", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, raw);
    }

    public async Task<(bool Ok, int Status, string Body)> SendFileAsync(
        string relativePath, byte[] bytes, string fileName, string contentType,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath);
        var project = _env.Get("BEACON_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.TryAddWithoutValidation("X-Project-Id", project.Trim());

        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        multipart.Add(file, "file", Path.GetFileName(fileName));
        request.Content = multipart;

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, raw);
    }

    public async Task<(bool Ok, int Status, string Body, string? ContentType, byte[]? Bytes)> DownloadFileAsync(
        string relativePath, int maxBytes, CancellationToken ct = default)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        var project = _env.Get("BEACON_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.TryAddWithoutValidation("X-Project-Id", project.Trim());

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return (false, (int)response.StatusCode, error, null, null);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            return (false, 413, $"Attachment exceeds the {maxBytes}-byte read limit.", null, null);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var buffer = new MemoryStream(Math.Min(maxBytes, 81920));
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes - total + 1)), ct);
            if (read == 0)
                break;
            if (total + read > maxBytes)
                return (false, 413, $"Attachment exceeds the {maxBytes}-byte read limit.", null, null);
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            total += read;
        }

        return (
            true,
            (int)response.StatusCode,
            string.Empty,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            buffer.ToArray());
    }
}
