namespace ProjectBeacon.Application.Auth;

using Microsoft.Extensions.Configuration;

public static class LocalAuthOptions
{
    public static bool InviteOnly(IConfiguration? configuration)
    {
        var raw = configuration?["AUTH_LOCAL_INVITE_ONLY"];
        return bool.TryParse(raw, out var value) && value;
    }

    public static string PublicUrl(IConfiguration? configuration)
    {
        var raw = configuration?["BEACON_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BEACON_PUBLIC_URL")
            ?? string.Empty;
        return raw.TrimEnd('/');
    }
}
