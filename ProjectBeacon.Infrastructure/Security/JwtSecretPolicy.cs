namespace ProjectBeacon.Infrastructure.Security;

public static class JwtSecretPolicy
{
    public const int MinLength = 32;

    public static string EnsureConfigured(string? secret, string environmentName)
    {
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException(
                    "JWT:Secret is not set. Set JWT__Secret to a random string of at least "
                    + $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
            if (secret.Length < MinLength)
                throw new InvalidOperationException(
                    $"JWT:Secret is too short ({secret.Length} characters). Set JWT__Secret to a random string of at least "
                    + $"{MinLength} characters, e.g. `openssl rand -base64 48`.");
        }

        return secret ?? throw new InvalidOperationException("JWT secret key not configured.");
    }
}
