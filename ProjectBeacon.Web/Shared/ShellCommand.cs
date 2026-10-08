namespace ProjectBeacon.Web.Shared;
/// <summary>
/// One command-palette entry: label, drawer group, route, and icon.
/// </summary>
public sealed record ShellCommand(string Label, string Group, string Href, string Icon);
