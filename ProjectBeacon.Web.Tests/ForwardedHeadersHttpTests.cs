namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task CookieLogin_UntrustedProxy_ForwardedProtoIgnored_DoesNotSetSecureCookie()
    {
        // An explicit trust list replaces the framework loopback defaults, so this
        // loopback remote is untrusted and the spoofed proto is ignored. Before the
        // fix the seeded loopback defaults stayed in the trust set and the proto was
        // honored.
        await using var factory = new ForwardedHeadersTestFactory(
            IPAddress.Loopback,
            ("ForwardedHeaders:KnownProxies:0", "10.9.9.9"));
        var response = await CookieLoginWithForwardedProtoAsync(factory);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.DoesNotContain("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieLogin_TrustedProxy_ForwardedProtoHonored_SetsSecureCookie()
    {
        await using var factory = new ForwardedHeadersTestFactory(
            UntrustedTestRemote,
            ("ForwardedHeaders:KnownProxies:0", "9.9.9.9"));
        var response = await CookieLoginWithForwardedProtoAsync(factory);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.Contains("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieLogin_TrustedNetwork_ForwardedProtoHonored_SetsSecureCookie()
    {
        await using var factory = new ForwardedHeadersTestFactory(
            UntrustedTestRemote,
            ("ForwardedHeaders:KnownNetworks:0", "9.9.9.9/32"));
        var response = await CookieLoginWithForwardedProtoAsync(factory);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.Contains("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieLogin_LoopbackDefaults_RemoteOutsideLoopback_ForwardedProtoIgnored_DoesNotSetSecureCookie()
    {
        // With no explicit trust configuration the framework loopback defaults apply,
        // so a remote outside loopback is untrusted.
        await using var factory = new ForwardedHeadersTestFactory(UntrustedTestRemote);
        var response = await CookieLoginWithForwardedProtoAsync(factory);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.DoesNotContain("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    // A non-loopback remote that no test trust list contains.
    private static readonly IPAddress UntrustedTestRemote = IPAddress.Parse("9.9.9.9");

    private static async Task<HttpResponseMessage> CookieLoginWithForwardedProtoAsync(WebTestFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var password = await GetBootstrapPasswordAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/cookie-login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        return await client.SendAsync(request);
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
        return await WebTestFactory.GetBootstrapPasswordAsync(client);
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

/// <summary>
/// WebTestFactory with an optional fixed socket remote IP and explicit ForwardedHeaders
/// trust-list configuration. The explicit list replaces the framework loopback defaults;
/// with no settings the defaults apply.
/// </summary>
public sealed class ForwardedHeadersTestFactory : WebTestFactory
{
    private readonly IPAddress? _remoteIp;
    private readonly (string Key, string Value)[] _settings;

    public ForwardedHeadersTestFactory(IPAddress? remoteIp = null, params (string Key, string Value)[] settings)
    {
        _remoteIp = remoteIp;
        _settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        if (_remoteIp is not null)
        {
            var remoteIp = _remoteIp;
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(remoteIp)));
        }
    }
}

/// <summary>
/// TestServer reports no socket remote IP, and the forwarded-headers middleware treats
/// a missing remote IP as trusted. This filter pins a real remote IP per request so the
/// trust check evaluates an actual address.
/// </summary>
public sealed class FixedRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = remoteIp;
                await next(context);
            });
            next(app);
        };
    }
}
