using System.Globalization;
using Bunit;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class SettingsRenderTests : BUnitRenderBase
{
    public SettingsRenderTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
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
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("Device", cut.Markup);
    }

    [Fact]
    public void Settings_RendersAccountNotProject()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("Account", cut.Markup);
        Assert.Contains("Agents", cut.Markup);
        Assert.Contains("Pick a service", cut.Markup);
        Assert.Contains("Own server", cut.Markup);
        Assert.Contains("Each service keeps one model", cut.Markup);
        Assert.Contains("Current password", cut.Markup);
        Assert.Contains("Change password", cut.Markup);
        Assert.DoesNotContain("ProjectFolder", cut.Markup);
        Assert.DoesNotContain("Members", cut.Markup);
        Assert.DoesNotContain("InviteMember", cut.Markup);
    }
}
