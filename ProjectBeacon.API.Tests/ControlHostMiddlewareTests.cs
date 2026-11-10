namespace ProjectBeacon.API.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ProjectBeacon.API.Auth;

public sealed class ControlHostMiddlewareTests
{
    private const string ControlHost = "control.projectbeacon.tech";

    [Theory]
    [InlineData("/admin/audit")]
    [InlineData("/admin/email")]
    [InlineData("/admin/operators")]
    [InlineData("/admin/organizations")]
    [InlineData("/admin/organizations/11111111-1111-1111-1111-111111111111")]
    [InlineData("/control/login")]
    [InlineData("/control/bootstrap")]
    [InlineData("/control/recover")]
    [InlineData("/control/logout")]
    [InlineData("/control/security")]
    [InlineData("/control/elevate")]
    [InlineData("/bootstrap")]
    [InlineData("/recover")]
    [InlineData("/v1/admin/audit")]
    [InlineData("/v1/auth/control-cookie-login")]
    [InlineData("/v1/auth/bootstrap")]
    [InlineData("/v1/auth/recover-admin")]
    public async Task CustomerHost_HidesControlPlaneRoutes(string path)
    {
        var context = Context("app.projectbeacon.tech", path);
        var nextCalled = false;
        var middleware = new ControlHostMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Configuration(),
            ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("/v1/admin/audit")]
    [InlineData("/admin/email")]
    [InlineData("/admin/organizations")]
    [InlineData("/admin/organizations/11111111-1111-1111-1111-111111111111")]
    [InlineData("/app.css")]
    [InlineData("/app-base.css")]
    [InlineData("/app-controls.css")]
    [InlineData("/app-workspace.css")]
    [InlineData("/app-content.css")]
    [InlineData("/app-shell.css")]
    [InlineData("/app-chat.css")]
    [InlineData("/app-chat-responsive.css")]
    [InlineData("/shell.js")]
    [InlineData("/brand/beacon-mark.svg")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/control/login")]
    [InlineData("/control/bootstrap")]
    [InlineData("/control/recover")]
    [InlineData("/control/logout")]
    [InlineData("/control/security")]
    [InlineData("/control/elevate")]
    public async Task ControlHost_AllowsAuditApiShellAssetsAndControlPages(string path)
    {
        var context = Context(ControlHost, path);
        var nextCalled = false;
        var middleware = new ControlHostMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Configuration(),
            ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/login")]
    [InlineData("/v1/auth/login")]
    [InlineData("/v1/tasks")]
    [InlineData("/v1/admin/future-export.csv")]
    [InlineData("/dashboard/index.html")]
    [InlineData("/future-export.csv")]
    public async Task ControlHost_DoesNotExposeCustomerOrUnlistedAdminRoutes(string path)
    {
        var context = Context(ControlHost, path);
        var nextCalled = false;
        var middleware = new ControlHostMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Configuration(),
            ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task ControlHostRoot_RedirectsToAuditConsole()
    {
        var context = Context(ControlHost, "/");
        var middleware = new ControlHostMiddleware(
            _ => Task.CompletedTask,
            Configuration(),
            ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/admin/audit", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/v1/admin/audit")]
    [InlineData("/v1/auth/control-cookie-login")]
    [InlineData("/v1/auth/bootstrap")]
    [InlineData("/v1/auth/recover-admin")]
    public async Task StandaloneApiHost_AlwaysHidesOperatorRoutes(string path)
    {
        var context = Context(ControlHost, path);
        var nextCalled = false;
        var middleware = new PublicApiOperatorRouteMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            ProductionEnvironment());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    private static DefaultHttpContext Context(string host, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        return context;
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Control:Host"] = ControlHost
            })
            .Build();

    private static IWebHostEnvironment ProductionEnvironment() => new TestHostEnvironment();

    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "ProjectBeacon.API.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
