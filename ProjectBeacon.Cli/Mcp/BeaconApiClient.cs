namespace ProjectBeacon.Cli.Mcp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

internal sealed class BeaconApiClient
{
    private readonly HttpClient _http;

    public BeaconApiClient(HttpClient http) => _http = http;

    public static BeaconApiClient? FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("BEACON_API_URL");
        var token = Environment.GetEnvironmentVariable("BEACON_API_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return null;
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            return null;

        var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return new BeaconApiClient(http);
    }

    public async Task<(bool Ok, int Status, string Body)> SendAsync(
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        var project = Environment.GetEnvironmentVariable("BEACON_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(project))
            request.Headers.TryAddWithoutValidation("X-Project-Id", project.Trim());
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, raw);
    }
}
