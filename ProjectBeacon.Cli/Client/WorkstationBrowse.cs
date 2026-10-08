namespace ProjectBeacon.Cli.Client;

using System.Text.Json;
using ProjectBeacon.Application.Common;

public static class WorkstationBrowse
{
    public static Result<string> ListDir(string root, string? path)
    {
        var resolved = WorkspacePath.ResolveInRoot(root, path, relativeOnly: false);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "missing root");

        var full = resolved.Value!;
        if (!Directory.Exists(full))
            return Result.Failure<string>($"directory not found: {full}");

        var parent = Directory.GetParent(full)?.FullName;
        var entries = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                entries.Add(new { name = Path.GetFileName(dir), path = dir, isDirectory = true });
            }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var file in Directory.EnumerateFiles(full).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(200))
        {
            entries.Add(new { name = Path.GetFileName(file), path = file, isDirectory = false });
        }
        return Result.Ok(JsonSerializer.Serialize(new { path = full, parent, entries }));
    }

    public static Result<string> ScanGguf(string root, string? path)
    {
        var resolved = WorkspacePath.ResolveInRoot(root, path, relativeOnly: false);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "missing root");

        var full = resolved.Value!;
        if (!Directory.Exists(full))
            return Result.Ok(JsonSerializer.Serialize(new { root = full, files = Array.Empty<object>() }));

        var files = Directory.EnumerateFiles(full, "*.gguf", SearchOption.AllDirectories)
            .Take(200)
            .Select(f => new { name = Path.GetFileName(f), path = f, bytes = new FileInfo(f).Length })
            .ToList();
        return Result.Ok(JsonSerializer.Serialize(new { root = full, files }));
    }
}
