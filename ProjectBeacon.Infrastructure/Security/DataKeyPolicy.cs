namespace ProjectBeacon.Infrastructure.Security;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Resolves the data-protection keys. The current key (<see cref="ConfigKey"/>) protects new (v2)
/// ciphertexts and is required in every environment that is not Development/Testing. The legacy
/// material protects pre-existing (v1) ciphertexts: an explicit <see cref="LegacyConfigKey"/> wins,
/// otherwise the JWT secret acts as a time-limited bridge, and safe modes fall back to the dev key.
/// The JWT secret is never the source of the current key.
/// </summary>
public static class DataKeyPolicy
{
    public const int MinLength = 32;
    public const string ConfigKey = "DATA_PROTECTION_KEY";
    public const string LegacyConfigKey = "DATA_PROTECTION_LEGACY_KEY";
    public const string WriteV2Key = "DATA_PROTECTION_WRITE_V2";
    public const string DevFallbackKey = "beacon-dev-data-key";

    /// <summary>Where the legacy v1 key material came from, for diagnostics.</summary>
    public enum LegacySource
    {
        None,
        Explicit,
        JwtBridge,
        DevFallback
    }

    public sealed record DataKeys(string Current, string? Legacy, LegacySource LegacySource, bool WriteV2);

    public static DataKeys ResolveDataKeys(IConfiguration configuration, string environmentName)
    {
        var safe = IsSafeMode(environmentName);
        var raw = configuration[ConfigKey];
        var legacy = configuration[LegacyConfigKey];
        var jwt = configuration["JWT:Secret"];
        var writeV2 = IsTruthy(configuration[WriteV2Key]);

        string current;
        if (safe)
        {
            current = string.IsNullOrWhiteSpace(raw) ? DevFallbackKey : raw!;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException(
                    "Data encryption key is not set. Set DATA_PROTECTION_KEY to a random string of at least " +
                    $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
            if (raw.Length < MinLength)
                throw new InvalidOperationException(
                    $"Data encryption key is too short ({raw.Length} characters). Set DATA_PROTECTION_KEY to a random string of at least " +
                    $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
            current = raw;
        }

        string? legacyMaterial = null;
        LegacySource source = LegacySource.None;
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            legacyMaterial = legacy!;
            source = LegacySource.Explicit;
        }
        else if (!string.IsNullOrWhiteSpace(jwt))
        {
            legacyMaterial = jwt!;
            source = LegacySource.JwtBridge;
        }
        else if (safe)
        {
            legacyMaterial = DevFallbackKey;
            source = LegacySource.DevFallback;
        }

        if (!safe && legacyMaterial is null && !writeV2)
            throw new InvalidOperationException(
                "Data protection cannot write v1 ciphertexts: no legacy key material is configured and " +
                "DATA_PROTECTION_WRITE_V2 is off. Set DATA_PROTECTION_LEGACY_KEY, or enable " +
                "DATA_PROTECTION_WRITE_V2 on a fresh install.");

        return new DataKeys(current, legacyMaterial, source, writeV2);
    }

    public static bool IsSafeMode(string environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
