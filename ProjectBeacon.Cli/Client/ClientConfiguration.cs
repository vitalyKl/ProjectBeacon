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
