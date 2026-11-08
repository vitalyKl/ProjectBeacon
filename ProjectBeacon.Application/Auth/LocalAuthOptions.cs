namespace ProjectBeacon.Application.Auth;

using Microsoft.Extensions.Configuration;
/// <summary>
/// Reads AUTH_LOCAL_INVITE_ONLY and BEACON_PUBLIC_URL. Invite-only is true only when the value parses as true.
/// </summary>
public static class LocalAuthOptions
{
    public static bool InviteOnly(IConfiguration? configuration)
    {
        // Public registration is intentionally unavailable for the hosted SaaS alpha.
        // Keep the legacy configuration key from accidentally opening self-service sign-up.
        return true;
    }

    public static string PublicUrl(IConfiguration? configuration)
    {
        var raw = configuration?["BEACON_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BEACON_PUBLIC_URL")
            ?? string.Empty;
        return raw.TrimEnd('/');
    }
}
