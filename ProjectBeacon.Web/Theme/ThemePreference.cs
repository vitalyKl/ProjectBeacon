namespace ProjectBeacon.Web.Theme;
/// <summary>
/// Stored appearance: Dark, Light, or System.
/// </summary>
public enum ThemePreference
{
    Dark,
    Light,
    System
}
/// <summary>
/// Parses the beacon-theme cookie. Unknown values are Dark. VersionCore strips a git suffix from the informational version.
/// </summary>
public static class ThemePreferenceResolver
{
    public const string CookieName = "beacon-theme";

    public static ThemePreference Parse(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemePreference.Light,
            "system" => ThemePreference.System,
            _ => ThemePreference.Dark
        };

    public static string Format(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.System => "system",
        _ => "dark"
    };

    public static bool Resolve(ThemePreference preference, bool systemIsDark) => preference switch
    {
        ThemePreference.Light => false,
        ThemePreference.System => systemIsDark,
        _ => true
    };

    public static string VersionCore(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "0";
        var raw = informational.Trim();
        var plus = raw.IndexOf('+');
        if (plus >= 0)
            raw = raw[..plus];
        var space = raw.IndexOf(' ');
        if (space >= 0)
            raw = raw[..space];
        return raw.Length == 0 ? "0" : raw;
    }
}
