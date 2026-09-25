namespace ProjectBeacon.Infrastructure.Security;

using System.Security.Cryptography;
using System.Text;

public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static string OtpAuthUri(string secret, string account) =>
        "otpauth://totp/" + Uri.EscapeDataString("Beacon:" + account) + "?secret=" + secret + "&issuer=Beacon&digits=6&period=30";

    public static bool Verify(string secret, string code, DateTime utcNow)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6)
            return false;
        var step = new DateTimeOffset(utcNow).ToUnixTimeSeconds() / 30;
        for (var skew = -1; skew <= 1; skew++)
        {
            if (Hotp(secret, step + skew) == digits)
                return true;
        }
        return false;
    }

    public static string Hotp(string secret, long counter)
    {
        var key = FromBase32(secret);
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xff);
            counter >>= 8;
        }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0f;
        var bin = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string secret)
    {
        var clean = secret.Trim().Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var buffer = 0;
        var bits = 0;
        var bytes = new List<byte>();
        foreach (var c in clean)
        {
            var val = Alphabet.IndexOf(c);
            if (val < 0)
                continue;
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return bytes.ToArray();
    }
}
