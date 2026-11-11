namespace ProjectBeacon.Application.Operations;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Shared request-signing format between the Control Plane and the isolated OpsAgent.
/// The caller signs only a method and exact path/query; no body or arbitrary target is accepted.
/// </summary>
public static class PlatformLogRequestSignature
{
    public const int MinimumSecretBytes = 32;

    public static string Create(
        ReadOnlySpan<byte> secret,
        string timestamp,
        string nonce,
        string method,
        string pathAndQuery)
    {
        if (secret.Length < MinimumSecretBytes)
            throw new ArgumentException("The OpsAgent shared secret must be at least 32 bytes.", nameof(secret));
        if (string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(nonce)
            || string.IsNullOrWhiteSpace(method)
            || string.IsNullOrWhiteSpace(pathAndQuery)
            || pathAndQuery[0] != '/')
            throw new ArgumentException("The signed request fields are invalid.");

        var canonical = string.Join('\n',
            timestamp,
            nonce,
            method.ToUpperInvariant(),
            pathAndQuery);
        var digest = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool Verify(
        ReadOnlySpan<byte> secret,
        string timestamp,
        string nonce,
        string method,
        string pathAndQuery,
        string suppliedSignature)
    {
        if (string.IsNullOrWhiteSpace(suppliedSignature))
            return false;

        string expected;
        try
        {
            expected = Create(secret, timestamp, nonce, method, pathAndQuery);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            var expectedBytes = Convert.FromHexString(expected);
            var actualBytes = Convert.FromHexString(suppliedSignature);
            return actualBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
