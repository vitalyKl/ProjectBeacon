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
        // RegistrationPolicy is the source of truth. The current runtime supports only the
        // operator-invited alpha flow; reserved future modes are not runtime-selectable.
        _ = configuration;
        return RegistrationPolicy.SaaSAlpha.Mode == RegistrationMode.InviteOnly;
    }

    public static string PublicUrl(IConfiguration? configuration)
    {
        var raw = configuration?["BEACON_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BEACON_PUBLIC_URL")
            ?? string.Empty;
        return raw.TrimEnd('/');
    }
}
