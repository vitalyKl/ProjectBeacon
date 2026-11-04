using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class SettingsRenderTests : BUnitRenderBase
{
    public SettingsRenderTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public void ProjectMcpServers_RendersForProjectOwner()
    {
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var authProvider = Bunit.Services.GetRequiredService<Bunit.TestDoubles.FakeAuthenticationStateProvider>();
        authProvider.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())
            },
            "TestAuth");

        var cut = Bunit.RenderComponent<Features.Agents.ProjectMcpServers>(parameters =>
            parameters.Add(component => component.ProjectId, project.Id));

        Assert.Contains("beacon-panel", cut.Markup);
    }

    [Fact]
    public void Settings_RendersPage()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("<div", cut.Markup);
    }

    [Fact]
    public void Settings_RendersDeviceSection()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Workstations>();

        Assert.Contains("Device", cut.Markup);
    }

    [Fact]
    public void Settings_RendersAccountNotProject()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("Account", cut.Markup);
        Assert.Contains("Appearance", cut.Markup);
        Assert.Contains("Dark", cut.Markup);
        Assert.Contains("Light", cut.Markup);
        Assert.Contains("System", cut.Markup);
        Assert.Contains("Current password", cut.Markup);
        Assert.Contains("Change password", cut.Markup);
        Assert.DoesNotContain("ProjectFolder", cut.Markup);
        Assert.DoesNotContain("Members", cut.Markup);
        Assert.DoesNotContain("InviteMember", cut.Markup);
    }
}
