namespace ProjectBeacon.Infrastructure.Security;
/// <summary>
/// Resolves the secret-protection data key from configuration (JWT:Secret). The development fallback is
/// only used in the explicit safe modes (Development, Testing); every other environment is fail-closed.
/// </summary>
public static class DataKeyPolicy
{
    public const int MinLength = 32;
    public const string ConfigKey = "JWT:Secret";
    public const string DevFallbackKey = "beacon-dev-data-key";

    public static string EnsureConfigured(string? raw, string environmentName)
    {
        if (IsSafeMode(environmentName))
            return string.IsNullOrWhiteSpace(raw) ? DevFallbackKey : raw!;

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Data encryption key is not set. Set JWT__Secret to a random string of at least "
                + $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
        if (raw.Length < MinLength)
            throw new InvalidOperationException(
                $"Data encryption key is too short ({raw.Length} characters). Set JWT__Secret to a random string of at least "
                + $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
        return raw;
    }

    private static bool IsSafeMode(string environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
}
