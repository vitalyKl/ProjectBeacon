namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

internal sealed class CodeTree
{
    private readonly CodeWorkspace _files;

    public CodeTree(CodeWorkspace files) => _files = files;

    public Result<TreeResult> GetTree(string? subPath, int maxEntries)
    {
        if (!Directory.Exists(_files.Root))
            return Result.Failure<TreeResult>("directory not found");

        if (maxEntries <= 0)
            maxEntries = CodeIndex.DefaultMaxEntries;

        string start = _files.Root;
        if (!string.IsNullOrWhiteSpace(subPath))
        {
            var resolved = WorkspacePath.ResolveInsideRoot(_files.Root, subPath);
            if (!resolved.Success)
                return Result.Failure<TreeResult>(resolved.Error ?? "invalid path");
            if (!Directory.Exists(resolved.Value))
                return Result.Failure<TreeResult>("directory not found");
            start = resolved.Value!;
        }

        var entries = new List<TreeEntry>();
        var truncated = false;
        CollectTree(start, maxEntries, entries, ref truncated);
        return Result.Ok(new TreeResult(_files.Root, entries, truncated));
    }

    private void CollectTree(string dir, int max, List<TreeEntry> entries, ref bool truncated)
    {
        foreach (var sub in _files.EnumerateDirectories(dir))
        {
            if (_files.IsIgnoredDirectory(sub))
                continue;
            if (entries.Count >= max)
            {
                truncated = true;
                return;
            }
            entries.Add(MakeEntry(sub, isDirectory: true));
            CollectTree(sub, max, entries, ref truncated);
            if (truncated)
                return;
        }

        foreach (var file in _files.EnumerateFiles(dir))
        {
            if (entries.Count >= max)
            {
                truncated = true;
                return;
            }
            entries.Add(MakeEntry(file, isDirectory: false));
        }
    }

    private TreeEntry MakeEntry(string fullPath, bool isDirectory)
    {
        long size = 0;
        if (!isDirectory)
        {
            try
            {
                size = new FileInfo(fullPath).Length;
            }
            catch (IOException)
            {
            }
        }

        return new TreeEntry(_files.ToRelative(fullPath), isDirectory, size);
    }
}
