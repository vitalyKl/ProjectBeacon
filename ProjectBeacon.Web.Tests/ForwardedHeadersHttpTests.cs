namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class ForwardedHeadersHttpTests
{
    [Fact]
    public async Task CookieLogin_ForwardedProtoHttps_SetsSecureCookie()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var password = await GetBootstrapPasswordAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/cookie-login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.Contains("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieLogin_PlainHttp_DoesNotSetSecureCookie()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var password = await GetBootstrapPasswordAsync(client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });

        var response = await client.PostAsync("/v1/auth/cookie-login", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.DoesNotContain("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> GetBootstrapPasswordAsync(HttpClient client)
    {
        var boot = await client.PostAsJsonAsync("/v1/auth/bootstrap", new { });
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<JsonElement>();
        return bootJson.GetProperty("password").GetString()!;
    }

    private static string[] GetBeaconAuthCookieAttributes(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(value => value.StartsWith("BeaconAuth=", StringComparison.Ordinal));
        Assert.NotNull(setCookie);
        var tail = setCookie.Contains(';') ? setCookie[(setCookie.IndexOf(';') + 1)..] : string.Empty;
        return tail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
