using ProjectBeacon.Web.Theme;

namespace ProjectBeacon.Web.Tests;

public sealed class ThemePreferenceTests
{
    [Theory]
    [InlineData(null, ThemePreference.Dark)]
    [InlineData("", ThemePreference.Dark)]
    [InlineData("nope", ThemePreference.Dark)]
    [InlineData("dark", ThemePreference.Dark)]
    [InlineData("DARK", ThemePreference.Dark)]
    [InlineData("light", ThemePreference.Light)]
    [InlineData(" system ", ThemePreference.System)]
    public void Parse_DefaultsToDark(string? raw, ThemePreference expected)
    {
        Assert.Equal(expected, ThemePreferenceResolver.Parse(raw));
    }

    [Theory]
    [InlineData(ThemePreference.Dark, false, true)]
    [InlineData(ThemePreference.Light, true, false)]
    [InlineData(ThemePreference.System, true, true)]
    [InlineData(ThemePreference.System, false, false)]
    public void Resolve_UsesSystemOnlyForSystem(ThemePreference preference, bool systemIsDark, bool expected)
    {
        Assert.Equal(expected, ThemePreferenceResolver.Resolve(preference, systemIsDark));
    }

    [Theory]
    [InlineData(ThemePreference.Dark, "dark")]
    [InlineData(ThemePreference.Light, "light")]
    [InlineData(ThemePreference.System, "system")]
    public void Format_RoundTrips(ThemePreference preference, string raw)
    {
        Assert.Equal(raw, ThemePreferenceResolver.Format(preference));
        Assert.Equal(preference, ThemePreferenceResolver.Parse(raw));
    }

    [Theory]
    [InlineData(null, "0")]
    [InlineData("  ", "0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0+abc", "0.2.0")]
    [InlineData("0.2.0 sha", "0.2.0")]
    public void VersionCore_StripsMetadata(string? informational, string expected)
    {
        Assert.Equal(expected, ThemePreferenceResolver.VersionCore(informational));
    }

    [Fact]
    public void RootCss_DefinesOnAccentForBothThemes()
    {
        var css = DesignTokens.RootCss;
        Assert.Contains("--color-on-accent: #ffffff;", css);
        Assert.Contains("body:has(.beacon-theme-dark)", css);
        var dark = css.Split("body:has(.beacon-theme-dark)", 2)[1];
        Assert.Contains("--color-on-accent: #10141c;", dark);
        Assert.Contains("--color-on-danger: #2a1013;", dark);
    }
}
