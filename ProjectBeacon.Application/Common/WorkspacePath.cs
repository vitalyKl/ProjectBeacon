namespace ProjectBeacon.Application.Common;

public static class WorkspacePath
{
    public const string RelativePathRequired = "path must be relative to the project folder.";

    public static Result<string> ResolveInRoot(string root, string? path, bool relativeOnly)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Result.Failure<string>("missing root");

        if (path is not null && path.Contains('\0', StringComparison.Ordinal))
            return Result.Failure<string>("malformed path");

        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            try
            {
                return Result.Ok(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result.Failure<string>("malformed path");
            }
        }

        if (Path.IsPathRooted(path))
        {
            if (relativeOnly)
                return Result.Failure<string>(RelativePathRequired);
            return ValidateAbsoluteInsideRoot(root, path);
        }

        return ResolveInsideRoot(root, path);
    }

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

    public static Result<string> ValidateAbsoluteInsideRoot(string root, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result.Failure<string>("missing path");

        if (absolutePath.Contains('\0', StringComparison.Ordinal))
            return Result.Failure<string>("malformed path");

        var rootFull = Path.GetFullPath(root);
        var pathFull = Path.GetFullPath(absolutePath);

        if (HasReparsePointBelowRoot(pathFull, rootFull))
            return Result.Failure<string>("path escapes project root");

        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

        if (!pathFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !pathFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<string>("path escapes project root");
        }

        return Result.Ok(pathFull);
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
