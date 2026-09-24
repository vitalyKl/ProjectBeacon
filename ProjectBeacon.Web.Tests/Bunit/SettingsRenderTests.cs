using Bunit;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class SettingsRenderTests : BUnitRenderBase
{
    [Fact]
    public void Settings_RendersPage()
    {
        // Act
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        // Assert
        Assert.Contains("<div", cut.Markup);
    }

    [Fact]
    public void Settings_RendersDeviceSection()
    {
        // Act
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        // Assert
        var html = cut.Markup;
        Assert.Contains("Device", html);
    }

    [Fact]
    public void Settings_RendersMembersSection()
    {
        // Act
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        // Assert
        var html = cut.Markup;
        Assert.Contains("Member", html);
    }
}
