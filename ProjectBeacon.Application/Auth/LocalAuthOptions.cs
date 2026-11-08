namespace ProjectBeacon.Application.Auth;

using Microsoft.Extensions.Configuration;
/// <summary>
/// Applies the hosted SaaS alpha registration policy and reads BEACON_PUBLIC_URL.
/// Public registration is not a runtime configuration option.
/// </summary>
public static class LocalAuthOptions
{
    public static bool InviteOnly(IConfiguration? configuration)
    {
        // The legacy opt-out is honored only by an explicitly marked test host.
        // Development, staging and production deployments always remain invite-only.
        var environment = configuration?["ASPNETCORE_ENVIRONMENT"];
        if (!string.Equals(environment, "Testing", StringComparison.OrdinalIgnoreCase))
            return true;

        var raw = configuration?["AUTH_LOCAL_INVITE_ONLY"];
        return !bool.TryParse(raw, out var inviteOnly) || inviteOnly;
    }

    public static string PublicUrl(IConfiguration? configuration)
    {
        var raw = configuration?["BEACON_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BEACON_PUBLIC_URL")
            ?? string.Empty;
        return raw.TrimEnd('/');
    }
}
