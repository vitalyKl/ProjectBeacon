namespace ProjectBeacon.Application.CodeIndex;

using System.Diagnostics;
using System.Text.Json;
using Application.Common;

internal sealed class GitScope
{
    private readonly CodeWorkspace _files;

    public GitScope(CodeWorkspace files) => _files = files;

    public Result<IReadOnlyList<string>> GetChangedFiles()
    {
        if (!Directory.Exists(_files.Root))
            return Result.Failure<IReadOnlyList<string>>("directory not found");

        var status = RunGit("status", "--porcelain");
        if (!status.Success)
            return Result.Failure<IReadOnlyList<string>>(status.Error ?? "git failed");

        var files = ParsePorcelain(status.Value!);
        return Result.Ok<IReadOnlyList<string>>(files);
    }

    public Result<IReadOnlyList<string>> GetChangedScope(IReadOnlyCollection<string>? paths, int maxFiles)
    {
        if (CodeWorkspace.FirstRootedPrefix(paths) is not null)
            return Result.Failure<IReadOnlyList<string>>(WorkspacePath.RelativePathRequired);

        var changed = GetChangedFiles();
        if (!changed.Success)
            return changed;

        var scoped = FilterByScope(changed.Value!, paths).Value!;
        var limited = maxFiles > 0 ? scoped.Take(maxFiles).ToList() : scoped;
        return Result.Ok<IReadOnlyList<string>>(limited);
    }

    public static Result<List<string>> FilterByScope(IReadOnlyCollection<string> files, IReadOnlyCollection<string>? prefixes)
    {
        if (CodeWorkspace.FirstRootedPrefix(prefixes) is not null)
            return Result.Failure<List<string>>(WorkspacePath.RelativePathRequired);

        var normalized = CodeWorkspace.NormalizePrefixes(prefixes);
        if (normalized.Length == 0)
            return Result.Ok(files.ToList());

        return Result.Ok(files
            .Where(f => normalized.Any(p => PathMatcher.MatchesPrefix(f, p)))
            .ToList());
    }

    public static List<string> ParsePorcelain(string output)
    {
        var files = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4)
                continue;

            var path = line[3..];
            if (path.Length == 0)
                continue;

            if (path.StartsWith("\"", StringComparison.Ordinal))
            {
                try
                {
                    var decoded = JsonDocument.Parse(path).RootElement.GetString();
                    if (!string.IsNullOrEmpty(decoded))
                        path = decoded;
                }
                catch (JsonException)
                {
                }
            }

            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..];

            var posix = path.Replace('\\', '/').TrimStart('/');
            if (posix.Length > 0)
                files.Add(posix);
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Result<string> RunGit(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _files.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return Result.Failure<string>("git is not available");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return Result.Failure<string>("git timed out");
            }

            var output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
                return Result.Failure<string>("not a git repository");

            return Result.Ok(output);
        }
        catch (Exception)
        {
            return Result.Failure<string>("git is not available");
        }
    }
}
