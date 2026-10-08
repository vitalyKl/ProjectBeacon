namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

internal enum FileReadError
{
    NotFound,
    TooLarge,
    Binary,
    Unreadable
}

internal readonly record struct FileRead(string? Text, FileReadError? Error)
{
    public static FileRead Ok(string text) => new(text, null);
    public static FileRead Fail(FileReadError error) => new(null, error);
}

internal sealed class CodeWorkspace
{
    public const int MaxFileSize = 1_000_000;
    public const int BinaryProbeSize = 8192;
    public const int MaxDisplayLine = 200;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out",
        "coverage", "target", ".vs", ".idea", ".gradle"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp", ".pdf",
        ".zip", ".gz", ".tar", ".7z", ".rar", ".dll", ".exe", ".pdb", ".so",
        ".dylib", ".a", ".o", ".bin", ".wasm", ".ttf", ".otf", ".woff",
        ".woff2", ".eot", ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac",
        ".parquet", ".pkl", ".pt", ".onnx", ".h5", ".npz", ".sqlite", ".db",
        ".iso", ".cab", ".msi", ".apk", ".model"
    };

    public CodeWorkspace(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    public string ToRelative(string fullPath)
        => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public bool IsIgnoredDirectory(string fullPath)
        => IgnoredDirectories.Contains(Path.GetFileName(fullPath));

    public static string[] NormalizePrefixes(IReadOnlyCollection<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0)
            return [];

        return prefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathMatcher.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? FirstRootedPrefix(IReadOnlyCollection<string>? prefixes)
    {
        if (prefixes is null)
            return null;

        foreach (var prefix in prefixes)
            if (WorkspacePath.IsRootedPortable(prefix))
                return prefix;

        return null;
    }

    public FileRead TryReadTextFile(string fullPath, long maxBytes)
    {
        if (!File.Exists(fullPath))
            return FileRead.Fail(FileReadError.NotFound);

        long size;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            return FileRead.Fail(FileReadError.Unreadable);
        }

        if (size > maxBytes)
            return FileRead.Fail(FileReadError.TooLarge);

        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)) || ContainsNulByte(fullPath))
            return FileRead.Fail(FileReadError.Binary);

        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (Exception)
        {
            return FileRead.Fail(FileReadError.Unreadable);
        }

        return FileRead.Ok(content);
    }

    public IReadOnlyList<string> EnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool ContainsNulByte(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[Math.Min(BinaryProbeSize, MaxFileSize)];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                    return true;
            }
        }
        catch (IOException)
        {
            return true;
        }

        return false;
    }
}
