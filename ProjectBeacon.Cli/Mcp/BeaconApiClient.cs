namespace ProjectBeacon.Cli.Mcp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

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
        var url = lookup.Get("BEACON_API_URL");
        var token = lookup.Get("BEACON_API_TOKEN");
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        if (hasUrl != hasToken)
            return new Resolution(null, true);
        if (!hasUrl)
            return new Resolution(null, false);
        if (!Uri.TryCreate(url!.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            return new Resolution(null, true);

        var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
        return new Resolution(new BeaconApiClient(http, lookup), false);
    }

    public async Task<(bool Ok, int Status, string Body)> SendAsync(
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        var project = _env.Get("BEACON_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.TryAddWithoutValidation("X-Project-Id", project.Trim());
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
}
