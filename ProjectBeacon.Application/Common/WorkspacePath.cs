namespace ProjectBeacon.Application.Common;

public static class WorkspacePath
{
    public static Result<string> ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Result.Failure<string>("missing path");

        if (relativePath.Contains('\0', StringComparison.Ordinal))
            return Result.Failure<string>("malformed path");

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        if (HasReparsePointBelowRoot(combined, rootFull))
            return Result.Failure<string>("path escapes project root");

        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

        if (!combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<string>("path escapes project root");
        }

        return Result.Ok(combined);
    }

    private static bool HasReparsePointBelowRoot(string path, string rootFull)
    {
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var current = path;
        while (!string.IsNullOrEmpty(current) &&
               current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return false;
    }
}
