namespace ProjectBeacon.Application.Mcp;

using Application.Common;

public sealed class FileWorkspace
{
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

    public Result<bool> WriteFile(string relativePath, string content)
    {
        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success || resolved.Value is null)
            return Result.Failure<bool>(resolved.Error ?? "invalid path");

        var dir = Path.GetDirectoryName(resolved.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(resolved.Value, content);
        return Result.Ok(true);
    }

    public Result<bool> ApplyPatch(string relativePath, string oldText, string newText)
    {
        if (oldText is null || newText is null || oldText.Length == 0)
            return Result.Failure<bool>("malformed patch");

        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success)
            return Result.Failure<bool>(resolved.Error ?? "invalid path");

        if (!File.Exists(resolved.Value))
            return Result.Failure<bool>("file not found");

        var current = File.ReadAllText(resolved.Value);
        var first = current.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return Result.Failure<bool>("patch does not apply");

        var second = current.IndexOf(oldText, first + Math.Max(oldText.Length, 1), StringComparison.Ordinal);
        if (second >= 0)
            return Result.Failure<bool>("patch is ambiguous");

        var updated = current.Remove(first, oldText.Length).Insert(first, newText);
        File.WriteAllText(resolved.Value, updated);
        return Result.Ok(true);
    }
}
