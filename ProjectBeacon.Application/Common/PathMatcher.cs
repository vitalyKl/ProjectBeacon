namespace ProjectBeacon.Application.Common;

public static class PathMatcher
{
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

    public static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim();
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed[2..];
        return trimmed.Trim('/');
    }
}
