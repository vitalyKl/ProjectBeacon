namespace ProjectBeacon.Web.Tests;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Web.Authentication;

public sealed class ControlPlaneHostGuardTests
{
    [Fact]
    public void ControlHostname_IsAllowed()
    {
        var navigation = new TestNavigationManager(
            "https://control.projectbeacon.tech/",
            "https://control.projectbeacon.tech/admin/audit");

        Assert.True(ControlPlaneHostGuard.IsCurrentHost(navigation, Configuration("control.projectbeacon.tech")));
    }

    [Fact]
    public void CustomerHostname_IsRejectedEvenForControlRoute()
    {
        var navigation = new TestNavigationManager(
            "https://app.projectbeacon.tech/",
            "https://app.projectbeacon.tech/admin/audit");

        Assert.False(ControlPlaneHostGuard.IsCurrentHost(navigation, Configuration("control.projectbeacon.tech")));
    }

    [Fact]
    public void LocalControlHostname_IsConfigurable()
    {
        var navigation = new TestNavigationManager(
            "http://control.localhost:5083/",
            "http://control.localhost:5083/control/login");

        Assert.True(ControlPlaneHostGuard.IsCurrentHost(navigation, Configuration("control.localhost")));
    }

    private static IConfiguration Configuration(string controlHost) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Control:Host"] = controlHost
            })
            .Build();

    private sealed class TestNavigationManager(string baseUri, string uri) : NavigationManager
    {
        public TestNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
