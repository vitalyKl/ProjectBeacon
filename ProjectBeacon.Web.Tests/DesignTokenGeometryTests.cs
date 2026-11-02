using ProjectBeacon.Web.Theme;

namespace ProjectBeacon.Web.Tests;

public sealed class DesignTokenGeometryTests
{
    [Fact]
    public void BeaconTheme_UsesSharedShellGeometryTokens()
    {
        Assert.Equal(DesignTokens.AppbarHeight, BeaconTheme.Theme.LayoutProperties.AppbarHeight);
        Assert.Equal(DesignTokens.DrawerWidthLeft, BeaconTheme.Theme.LayoutProperties.DrawerWidthLeft);
        Assert.Contains("--shell-gutter: 26px;", DesignTokens.RootCss);
        Assert.Contains("--content-max-width: 1680px;", DesignTokens.RootCss);
        Assert.Contains("--totp-qr-size: 180px;", DesignTokens.RootCss);
        Assert.Contains("--report-filter-max-width: 420px;", DesignTokens.RootCss);
    }
}
