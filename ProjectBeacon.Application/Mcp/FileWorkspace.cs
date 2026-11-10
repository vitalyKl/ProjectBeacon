namespace ProjectBeacon.Application.Mcp;

using System.Security.Cryptography;
using Application.Common;
/// <summary>A bounded character page from a workspace file.</summary>
public sealed record FileReadPage(string Content, int Offset, int Limit, int TotalCharacters, int NextOffset)
{
    public bool HasMore => NextOffset < TotalCharacters;
}

/// <summary>
/// Reads and writes files inside a root. Rejects escape, a missing file, and a patch that does not match exactly once.
/// </summary>
public sealed class FileWorkspace
{
    public const int DefaultReadPageSize = 12 * 1024;
    public const int MaxReadPageSize = 12 * 1024;

    public string Root { get; }

    public FileWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public Result<string> ReadFile(string relativePath)
    {
        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success)
            return resolved;

        if (!File.Exists(resolved.Value))
            return Result.Failure<string>("file not found");

        return Result.Ok(File.ReadAllText(resolved.Value));
    }

    /// <summary>
    /// Reads a bounded page by zero-based UTF-16 character offset. Pages stay below the
    /// native runtime's 16 KiB tool-result cap and include an explicit continuation offset.
    /// </summary>
    public Result<FileReadPage> ReadFilePage(string relativePath, int offset = 0, int limit = DefaultReadPageSize)
    {
        if (offset < 0)
            return Result.Failure<FileReadPage>("offset must be a non-negative character offset");
        if (limit is < 1 or > MaxReadPageSize)
            return Result.Failure<FileReadPage>($"limit must be between 1 and {MaxReadPageSize} characters");

        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success || resolved.Value is null)
            return Result.Failure<FileReadPage>(resolved.Error ?? "invalid path");
        if (!File.Exists(resolved.Value))
            return Result.Failure<FileReadPage>("file not found");

        var text = File.ReadAllText(resolved.Value);
        var start = Math.Min(offset, text.Length);
        // Never split a UTF-16 surrogate pair at either page boundary.
        if (start > 0 && start < text.Length
            && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            start--;

        var end = Math.Min(text.Length, start + limit);
        if (end > start && end < text.Length
            && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            if (end == start + 1)
                end++;
            else
                end--;
        }

        var content = text[start..end];
        return Result.Ok(new FileReadPage(content, start, limit, text.Length, end));
    }

    /// <summary>Computes SHA-256 over the exact file bytes without returning file content.</summary>
    public Result<string> HashFile(string relativePath)
    {
        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success || resolved.Value is null)
            return Result.Failure<string>(resolved.Error ?? "invalid path");
        if (!File.Exists(resolved.Value))
            return Result.Failure<string>("file not found");

        var hash = SHA256.HashData(File.ReadAllBytes(resolved.Value));
        return Result.Ok(Convert.ToHexString(hash).ToLowerInvariant());
    }

    public Result WriteFile(string relativePath, string content)
    {
        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success || resolved.Value is null)
            return Result.Failure(resolved.Error ?? "invalid path");

        var dir = Path.GetDirectoryName(resolved.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(resolved.Value, content);
        return Result.Ok();
    }

    public Result ApplyPatch(string relativePath, string oldText, string newText)
    {
        if (oldText is null || newText is null || oldText.Length == 0)
            return Result.Failure("malformed patch");

        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success)
            return Result.Failure(resolved.Error ?? "invalid path");

        if (!File.Exists(resolved.Value))
            return Result.Failure("file not found");

        var current = File.ReadAllText(resolved.Value);
        var first = current.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return Result.Failure("patch does not apply");

        var second = current.IndexOf(oldText, first + Math.Max(oldText.Length, 1), StringComparison.Ordinal);
        if (second >= 0)
            return Result.Failure("patch is ambiguous");

        var updated = current.Remove(first, oldText.Length).Insert(first, newText);
        File.WriteAllText(resolved.Value, updated);
        return Result.Ok();
    }
}
