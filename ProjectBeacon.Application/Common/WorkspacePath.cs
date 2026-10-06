namespace ProjectBeacon.Application.Common;

public static class WorkspacePath
{
    public const string RelativePathRequired = "path must be relative to the project folder.";
    public const string ParentSegmentNotAllowed = "path must not contain '..' segments.";

    // OS-independent rootedness: the control plane (Linux) must reject Windows absolute
    // paths even when it itself runs on Linux, so this never uses System.IO.Path.
    public static bool IsRootedPortable(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (path[0] == '/')
            return true;

        if (path.Length > 1 && path[0] == '\\' && path[1] == '\\')
            return true;

        if (path.Length > 1 && IsDriveLetter(path[0]) && path[1] == ':')
        {
            // "C:" is rooted; "C:\x"/"C:/x" are rooted; "C:x" (drive-relative) is not.
            return path.Length == 2 || path[2] == '/' || path[2] == '\\';
        }

        return false;
    }

    private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    // Both separators are significant for validation so traversal is rejected identically
    // on Linux and Windows, even where the other separator is a legal filename character.
    public static bool HasParentSegment(string path)
    {
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..")
                return true;
        }

        return false;
    }

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

        if (IsRootedPortable(path))
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

        if (IsRootedPortable(relativePath))
            return Result.Failure<string>(RelativePathRequired);

        if (HasParentSegment(relativePath))
            return Result.Failure<string>(ParentSegmentNotAllowed);

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
                if (Directory.Exists(current))
                {
                    // LinkTarget uses lstat on Unix, so it detects the link itself even
                    // when GetAttributes reports the target's metadata.
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint) ||
                        new DirectoryInfo(current).LinkTarget is not null)
                    {
                        return true;
                    }
                }
                else if (File.Exists(current) &&
                    (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint) ||
                     new FileInfo(current).LinkTarget is not null))
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
