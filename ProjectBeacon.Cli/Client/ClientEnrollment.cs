namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public static class ClientEnrollment
{
    public readonly record struct EnrollResult(bool Ok, Guid Id, string Token, string? Error);

    public static async Task<(bool Ok, string Message)> ProbeUrlAsync(string url, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = CreateClient(url, handler);
            using var response = await http.GetAsync("/v1/version", ct);
            return response.IsSuccessStatusCode
                ? (true, $"Connected ({(int)response.StatusCode})")
                : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<EnrollResult> EnrollAsync(
        string url, string login, string password, string name, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = CreateClient(url, handler);
            var loginRes = await http.PostAsJsonAsync("/v1/auth/login", new { login, password }, ct);
            if (!loginRes.IsSuccessStatusCode)
                return new EnrollResult(false, Guid.Empty, "", "Login failed.");
            var loginJson = await loginRes.Content.ReadFromJsonAsync<JsonElement>(ct);
            var jwt = loginJson.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(jwt))
                return new EnrollResult(false, Guid.Empty, "", "Login failed.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var create = await http.PostAsJsonAsync("/v1/devices", new { name, fingerprint = ClientHost.Fingerprint() }, ct);
            if (!create.IsSuccessStatusCode)
            {
                var body = await create.Content.ReadAsStringAsync(ct);
                return new EnrollResult(false, Guid.Empty, "", $"Enroll failed ({(int)create.StatusCode}) {body}".Trim());
            }
            var device = await create.Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = device.GetProperty("id").GetGuid();
            var token = device.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                return new EnrollResult(false, Guid.Empty, "", "Enroll failed.");
            return new EnrollResult(true, id, token, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EnrollResult(false, Guid.Empty, "", ex.Message);
        }
    }

    private static HttpClient CreateClient(string url, HttpMessageHandler? handler)
    {
        var http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }
}
