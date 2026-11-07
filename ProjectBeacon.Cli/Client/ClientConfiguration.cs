namespace ProjectBeacon.Cli.Client;

/// <summary>
/// Central resolution rules for workstation and MCP configuration. Command-line values override
/// environment values, which override persisted client.json values; blank values are treated as absent.
/// </summary>
internal static class ClientConfiguration
{
    internal readonly record struct ApiSettings(Uri? BaseUri, string? Token, bool Misconfigured)
    {
        public bool Configured => BaseUri is not null && !string.IsNullOrWhiteSpace(Token);
    }

    // Keep subprocess inheritance allowlists in one place. MCP children receive only the
    // minimal OS/runtime environment; native commands additionally need language toolchains.
    public static IReadOnlyList<string> McpSubprocessEnvironmentNames { get; } = Array.AsReadOnly(new[]
    {
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "TEMP", "TMP", "TMPDIR", "SystemRoot", "WINDIR",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"
    });

    public static IReadOnlyList<string> NativeCommandEnvironmentNames { get; } = Array.AsReadOnly(new[]
    {
        "PATH", "SystemRoot", "WINDIR", "COMSPEC",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_MULTILEVEL_LOOKUP",
        "JAVA_HOME", "RUSTUP_HOME", "RUSTUP_TOOLCHAIN", "GOROOT", "GOTOOLCHAIN",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "LANG", "LC_ALL",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER"
    });

    public static IReadOnlyDictionary<string, string> CaptureAllowedEnvironment(
        IEnumerable<string> allowedNames,
        Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(allowedNames);
        ArgumentNullException.ThrowIfNull(lookup);

        var values = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in allowedNames)
        {
            var value = lookup(name);
            if (value is not null)
                values[name] = value;
        }

        return values;
    }

    public static string McpStartupDiagnostic(bool misconfigured, bool configured)
    {
        if (misconfigured)
            return "beacon mcp: set both BEACON_API_URL (or BEACON_URL) and BEACON_API_TOKEN (or BEACON_CLIENT_TOKEN), and use a valid HTTP(S) URL.";
        return configured
            ? "beacon mcp: authenticated API mode."
            : "beacon mcp: control-plane tools require BEACON_API_URL/BEACON_API_TOKEN (or BEACON_URL/BEACON_CLIENT_TOKEN); file tools remain available.";
    }

    public static string? ResolveValue(
        string? commandLine,
        string? environment,
        string? persisted = null)
    {
        if (!string.IsNullOrWhiteSpace(commandLine))
            return commandLine.Trim();
        if (!string.IsNullOrWhiteSpace(environment))
            return environment.Trim();
        return string.IsNullOrWhiteSpace(persisted) ? null : persisted.Trim();
    }

    public static bool IsEnabled(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal)
        || bool.TryParse(value, out var enabled) && enabled;

    public static ApiSettings ResolveApi(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        // API-specific names are canonical. Workstation names remain supported for older
        // local setups so daemon and MCP resolve the same credentials consistently.
        var url = ResolveValue(null, lookup("BEACON_API_URL"), lookup("BEACON_URL"));
        var token = ResolveValue(null, lookup("BEACON_API_TOKEN"), lookup("BEACON_CLIENT_TOKEN"));
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasToken = !string.IsNullOrWhiteSpace(token);

        if (hasUrl != hasToken)
            return new ApiSettings(null, null, true);
        if (!hasUrl)
            return new ApiSettings(null, null, false);
        if (!Uri.TryCreate(url!.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            return new ApiSettings(null, null, true);

        return new ApiSettings(baseUri, token, false);
    }
}
