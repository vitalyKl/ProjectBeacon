namespace ProjectBeacon.Application.CodeIndex;

using System.Security.Cryptography;
using System.Text;
using Application.Common;

internal sealed class CodeHash
{
    private readonly CodeWorkspace _files;

    public CodeHash(CodeWorkspace files) => _files = files;

    public Result<string> HashRange(string path, int startLine, int endLine)
    {
        if (startLine < 1)
            return Result.Failure<string>("startLine must be >= 1");
        if (endLine < startLine)
            return Result.Failure<string>("endLine must be >= startLine");

        var resolved = WorkspacePath.ResolveInsideRoot(_files.Root, path);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "invalid path");

        var read = _files.TryReadTextFile(resolved.Value!, CodeWorkspace.MaxFileSize);
        if (read.Error is { } error)
            return Result.Failure<string>(error switch
            {
                FileReadError.TooLarge => "file too large",
                FileReadError.Binary => "file is binary",
                _ => "file not found"
            });

        var lines = SplitLines(read.Text!);
        if (endLine > lines.Length)
            return Result.Failure<string>($"endLine exceeds file length ({lines.Length} lines)");

        var range = string.Join("\n", lines[(startLine - 1)..endLine]);
        return Result.Ok(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(range))).ToLowerInvariant());
    }

    private static string[] SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var lines = content.Split('\n');
        if (lines[^1].Length == 0)
            lines = lines[..^1];
        return lines;
    }
}
