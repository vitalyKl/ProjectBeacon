namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class ControlPlaneAuthenticationHttpTests
{
    [Fact]
    public async Task ControlCookie_AuthenticatesAuditApi_ButCustomerCookieDoesNot()
    {
        await using var factory = new ControlPlaneWebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var password = await BootstrapOnControlHostAsync(client);

        // A normal customer login issues BeaconAuth on the customer host.
        using var customerLogin = Request(HttpMethod.Post, "/v1/auth/cookie-login", "app.projectbeacon.tech");
        customerLogin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var customerLoginResponse = await client.SendAsync(customerLogin, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, customerLoginResponse.StatusCode);
        var customerCookie = GetCookie(customerLoginResponse, "BeaconAuth");
        Assert.NotNull(customerCookie);

        // The customer cookie is not accepted by the Control Plane authentication selector.
        using var customerCookieAudit = Request(HttpMethod.Get, "/v1/admin/audit", "control.localhost");
        customerCookieAudit.Headers.TryAddWithoutValidation("Cookie", customerCookie);
        var customerCookieResponse = await client.SendAsync(customerCookieAudit, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, customerCookieResponse.StatusCode);

        // A separate Control Plane login issues BeaconControlAuth.
        using var controlLogin = Request(HttpMethod.Post, "/v1/auth/control-cookie-login", "control.localhost");
        controlLogin.Headers.TryAddWithoutValidation("Origin", "http://control.localhost");
        controlLogin.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        controlLogin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var controlLoginResponse = await client.SendAsync(controlLogin, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, controlLoginResponse.StatusCode);
        Assert.Equal("/admin/audit", controlLoginResponse.Headers.Location?.ToString());
        var controlCookie = GetCookie(controlLoginResponse, "BeaconControlAuth");
        Assert.NotNull(controlCookie);
        var setCookie = controlLoginResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("BeaconControlAuth=", StringComparison.Ordinal));
        Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(HasCookie(controlLoginResponse, "BeaconAuth"));

        using var controlCookieAudit = Request(HttpMethod.Get, "/v1/admin/audit", "control.localhost");
        controlCookieAudit.Headers.TryAddWithoutValidation("Cookie", controlCookie);
        var controlCookieResponse = await client.SendAsync(controlCookieAudit, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, controlCookieResponse.StatusCode);
        var page = await controlCookieResponse.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(page.GetProperty("items").ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task CustomerHost_CannotUseControlLoginOrReachAuditApi()
    {
        await using var factory = new ControlPlaneWebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var password = await BootstrapOnControlHostAsync(client);

        using var controlLoginOnCustomerHost = Request(
            HttpMethod.Post, "/v1/auth/control-cookie-login", "app.projectbeacon.tech");
        controlLoginOnCustomerHost.Headers.TryAddWithoutValidation("Origin", "http://app.projectbeacon.tech");
        controlLoginOnCustomerHost.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        controlLoginOnCustomerHost.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var loginResponse = await client.SendAsync(
            controlLoginOnCustomerHost, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, loginResponse.StatusCode);
        Assert.False(HasCookie(loginResponse, "BeaconControlAuth"));

        using var auditOnCustomerHost = Request(
            HttpMethod.Get, "/v1/admin/audit", "app.projectbeacon.tech");
        var auditResponse = await client.SendAsync(
            auditOnCustomerHost, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, auditResponse.StatusCode);
    }

    [Fact]
    public async Task ControlLogin_RejectsSiblingSubdomainOrigin()
    {
        await using var factory = new ControlPlaneWebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var password = await BootstrapOnControlHostAsync(client);
        using var request = Request(
            HttpMethod.Post, "/v1/auth/control-cookie-login", "control.localhost");
        request.Headers.TryAddWithoutValidation("Origin", "http://app.localhost");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(HasCookie(response, "BeaconControlAuth"));
    }

    private static async Task<string> BootstrapOnControlHostAsync(HttpClient client)
    {
        using var request = Request(HttpMethod.Post, "/v1/auth/bootstrap", "control.localhost");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WebTestFactory.BootstrapToken);
        request.Content = JsonContent.Create(new { });

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        return json.GetProperty("password").GetString()!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string host)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        return request;
    }

    private static string? GetCookie(HttpResponseMessage response, string name)
    {
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
        return cookie?.Split(';', 2)[0];
    }

    private static bool HasCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.Any(value => value.StartsWith(name + "=", StringComparison.Ordinal));
}

public sealed class ControlPlaneWebTestFactory : WebTestFactory
{
    public ControlPlaneWebTestFactory()
    {
        Environment.SetEnvironmentVariable("BEACON_MIGRATE_ON_START", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("Control:Host", "control.localhost");
    }
}
