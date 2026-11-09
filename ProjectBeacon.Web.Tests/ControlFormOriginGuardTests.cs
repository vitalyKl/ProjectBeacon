namespace ProjectBeacon.Web.Tests;

using Microsoft.AspNetCore.Http;
using ProjectBeacon.Web.Authentication;

public sealed class ControlFormOriginGuardTests
{
    [Fact]
    public void SameOriginForm_IsAllowed()
    {
        var context = Context(
            "https",
            "control.projectbeacon.tech",
            "https://control.projectbeacon.tech",
            "same-origin");

        Assert.True(ControlFormOriginGuard.IsAllowed(context));
    }

    [Theory]
    [InlineData("https://app.projectbeacon.tech", "same-site")]
    [InlineData("https://attacker.example", "cross-site")]
    [InlineData("null", "cross-site")]
    public void DifferentOrOpaqueOrigin_IsRejected(string origin, string fetchSite)
    {
        var context = Context("https", "control.projectbeacon.tech", origin, fetchSite);

        Assert.False(ControlFormOriginGuard.IsAllowed(context));
    }

    [Fact]
    public void MissingOrigin_IsRejected()
    {
        var context = Context("https", "control.projectbeacon.tech", null, null);

        Assert.False(ControlFormOriginGuard.IsAllowed(context));
    }

    [Fact]
    public void JsonRequest_IsRejected()
    {
        var context = Context("https", "control.projectbeacon.tech",
            "https://control.projectbeacon.tech", "same-origin");
        context.Request.ContentType = "application/json";

        Assert.False(ControlFormOriginGuard.IsAllowed(context));
    }

    [Fact]
    public void SameOriginWithCrossSiteFetchMetadata_IsRejected()
    {
        var context = Context("https", "control.projectbeacon.tech",
            "https://control.projectbeacon.tech", "cross-site");

        Assert.False(ControlFormOriginGuard.IsAllowed(context));
    }

    private static DefaultHttpContext Context(string scheme, string host, string? origin, string? fetchSite)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        if (fetchSite is not null)
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return context;
    }
}
