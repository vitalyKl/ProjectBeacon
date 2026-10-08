namespace ProjectBeacon.Application.Common;

/// <summary>
/// Case-insensitive prefix match after separators are normalized to <c>/</c>, a leading <c>./</c> is stripped, and surrounding slashes are trimmed.
/// A path matches when it equals the prefix or continues with <c>/</c>. Blank inputs do not match.
/// </summary>
public static class PathMatcher
{
    /// <summary>True when <paramref name="filePath"/> equals <paramref name="prefix"/> or is nested under it.</summary>
    public static bool MatchesPrefix(string? filePath, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(prefix))
            return false;

        var path = Normalize(filePath);
        var pre = Normalize(prefix);
        if (pre.Length == 0)
            return false;

        if (path.Equals(pre, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(pre + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalizes separators to <c>/</c>, strips a leading <c>./</c>, and trims surrounding slashes.</summary>
    public static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim();
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed[2..];
        return trimmed.Trim('/');
    }
}
