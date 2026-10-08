using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Infrastructure.Data;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class ShellChromeTests : BUnitRenderBase
{
    public ShellChromeTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        Bunit.Services.AddSingleton<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
    }

    [Fact]
    public void MainLayout_RendersThemeAndFooterHierarchy()
    {
        var cut = Bunit.RenderComponent<Shared.MainLayout>();
        var markup = cut.Markup;

        Assert.Contains("beacon-theme-dark", markup);
        var command = markup.IndexOf("beacon-command-trigger", StringComparison.Ordinal);
        var theme = markup.IndexOf("beacon-tool", StringComparison.Ordinal);
        var avatar = markup.IndexOf("beacon-avatar", StringComparison.Ordinal);
        Assert.True(command >= 0 && theme > command && avatar > theme);
        var fleet = markup.IndexOf("beacon-fleet", StringComparison.Ordinal);
        var prefs = markup.IndexOf("beacon-prefs", StringComparison.Ordinal);
        var version = markup.IndexOf("beacon-version", StringComparison.Ordinal);
        Assert.True(fleet >= 0 && prefs > fleet && version > prefs);
        Assert.DoesNotContain("Appearance", markup);
        Assert.Contains("Language", markup);
        Assert.Contains("Beacon v", markup);
    }
}
