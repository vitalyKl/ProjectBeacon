namespace ProjectBeacon.Application.CodeIndex;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Application.Common;

public sealed record TreeEntry(string Path, bool IsDirectory, long Size);

public sealed record TreeResult(string Root, IReadOnlyList<TreeEntry> Entries, bool Truncated);

public sealed record SearchMatch(string Path, int Line, string Text);

public sealed record SearchResult(string Query, IReadOnlyList<SearchMatch> Matches, bool Truncated);

public sealed record SymbolSignature(string Name, string Kind, string Signature, int Line, string? DocComment);

public sealed record FileSignatures(string Path, IReadOnlyList<SymbolSignature> Symbols, string Backend, string? Error);

public sealed record SignatureResult(IReadOnlyList<FileSignatures> Files, string Backend);

public sealed record CallerSite(string Path, int Line, string Snippet, string? Symbol);

public sealed record CallerResult(string Symbol, IReadOnlyList<CallerSite> Callers, string Backend, bool? SolutionBuilds);

/// <summary>
/// Single implementation of the local code index. It caches nothing: every
/// query re-scans the working tree, so the index can never be silently stale.
/// </summary>
public sealed class CodeIndex
{
    public const int DefaultMaxEntries = 2000;
    public const int DefaultMaxMatches = 100;
    public const int DefaultMaxFiles = 10000;

    private const int MaxFileSize = 1_000_000;
    private const int BinaryProbeSize = 8192;
    private const int MaxDisplayLine = 200;

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

    public string Root { get; }

    private readonly ILanguageRegistry _registry;
    private readonly ISignatureBackend _roslyn;
    private readonly ISignatureBackend _treeSitter;

    public CodeIndex(string root) : this(root, LanguageRegistry.CreateDefault()) { }

    public CodeIndex(string root, ILanguageRegistry registry)
    {
        Root = Path.GetFullPath(root);
        _registry = registry;
        _roslyn = new RoslynSignatureBackend();
        _treeSitter = new TreeSitterSignatureBackend();
    }

    public Result<TreeResult> GetTree(string? subPath = null, int maxEntries = DefaultMaxEntries)
    {
        if (!Directory.Exists(Root))
            return Result.Failure<TreeResult>("directory not found");

        if (maxEntries <= 0)
            maxEntries = DefaultMaxEntries;

        string start = Root;
        if (!string.IsNullOrWhiteSpace(subPath))
        {
            var resolved = WorkspacePath.ResolveInsideRoot(Root, subPath);
            if (!resolved.Success)
                return Result.Failure<TreeResult>(resolved.Error ?? "invalid path");
            if (!Directory.Exists(resolved.Value))
                return Result.Failure<TreeResult>("directory not found");
            start = resolved.Value;
        }

        var entries = new List<TreeEntry>();
        var truncated = false;
        CollectTree(start, maxEntries, entries, ref truncated);
        return Result.Ok(new TreeResult(Root, entries, truncated));
    }

    public Result<IReadOnlyList<string>> GetChangedFiles()
    {
        if (!Directory.Exists(Root))
            return Result.Failure<IReadOnlyList<string>>("directory not found");

        var status = RunGit("status", "--porcelain");
        if (!status.Success)
            return Result.Failure<IReadOnlyList<string>>(status.Error ?? "git failed");

        var files = ParsePorcelain(status.Value!);
        return Result.Ok<IReadOnlyList<string>>(files);
    }

    public Result<IReadOnlyList<string>> GetChangedScope(IReadOnlyCollection<string>? paths = null, int maxFiles = DefaultMaxFiles)
    {
        var changed = GetChangedFiles();
        if (!changed.Success)
            return changed;

        var filtered = FilterByScope(changed.Value!, paths);
        var limited = maxFiles > 0 ? filtered.Take(maxFiles).ToList() : filtered;
        return Result.Ok<IReadOnlyList<string>>(limited);
    }

    public Result<SignatureResult> GetSignatures(IReadOnlyCollection<string>? paths = null, int maxFiles = DefaultMaxFiles)
    {
        if (paths is not null && paths.Count == 0)
            return Result.Ok(new SignatureResult([], ""));

        IReadOnlyCollection<string> targetFiles;

        if (paths is null)
        {
            var changed = GetChangedFiles();
            if (!changed.Success)
                return Result.Failure<SignatureResult>(changed.Error ?? "git failed");
            targetFiles = changed.Value!;
        }
        else
        {
            targetFiles = paths;
        }

        if (maxFiles > 0)
            targetFiles = targetFiles.Take(maxFiles).ToList();

        var results = new List<FileSignatures>();

        foreach (var file in targetFiles)
        {
            var language = _registry.Resolve(file);
            if (language is null)
            {
                results.Add(new FileSignatures(file, [], "unsupported", null));
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception)
            {
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            long size;
            try
            {
                size = new FileInfo(fullPath).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (size == 0 || size > MaxFileSize)
                continue;

            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (Exception)
            {
                continue;
            }

            var backend = BackendFor(language.Backend);
            results.Add(backend.Extract(language, file, content));
        }

        var backends = results
            .Select(r => r.Backend)
            .Where(b => b != "unsupported")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backendId = backends.Count == 0 ? "" : backends.Count == 1 ? backends[0] : "mixed";

        return Result.Ok(new SignatureResult(results, backendId));
    }

    private ISignatureBackend BackendFor(SignatureBackend kind) => kind switch
    {
        SignatureBackend.Roslyn => _roslyn,
        SignatureBackend.TreeSitter => _treeSitter,
        _ => throw new ArgumentException($"Unknown backend: {kind}", nameof(kind))
    };

    public Result<SearchResult> Search(string query, IReadOnlyCollection<string>? paths = null, int maxMatches = DefaultMaxMatches)
    {
        if (!Directory.Exists(Root))
            return Result.Failure<SearchResult>("directory not found");

        if (string.IsNullOrWhiteSpace(query))
            return Result.Failure<SearchResult>("missing query");

        if (maxMatches <= 0)
            maxMatches = DefaultMaxMatches;

        var prefixes = NormalizePrefixes(paths);
        var matches = new List<SearchMatch>();
        var earlyStop = false;
        SearchDirectory(Root, prefixes, query, maxMatches + 1, matches, ref earlyStop);
        var truncated = matches.Count > maxMatches;
        if (truncated)
            matches.RemoveRange(maxMatches, matches.Count - maxMatches);
        return Result.Ok(new SearchResult(query, matches, truncated));
    }

    public static List<string> FilterByScope(IReadOnlyCollection<string> files, IReadOnlyCollection<string>? prefixes)
    {
        var normalized = NormalizePrefixes(prefixes);
        if (normalized.Length == 0)
            return files.ToList();

        return files
            .Where(f => normalized.Any(p => PathMatcher.MatchesPrefix(f, p)))
            .ToList();
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

    private void CollectTree(string dir, int max, List<TreeEntry> entries, ref bool truncated)
    {
        var dirs = SafeEnumerateDirectories(dir);
        foreach (var sub in dirs)
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(sub)))
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

        foreach (var file in SafeEnumerateFiles(dir))
        {
            if (entries.Count >= max)
            {
                truncated = true;
                return;
            }
            entries.Add(MakeEntry(file, isDirectory: false));
        }
    }

    private void SearchDirectory(
        string dir,
        string[] prefixes,
        string query,
        int maxMatches,
        List<SearchMatch> matches,
        ref bool truncated)
    {
        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(sub)))
                continue;
            if (matches.Count >= maxMatches)
            {
                truncated = true;
                return;
            }
            if (prefixes.Length > 0 && !DirectoryMatchesScope(sub, prefixes))
                continue;
            SearchDirectory(sub, prefixes, query, maxMatches, matches, ref truncated);
            if (truncated)
                return;
        }

        foreach (var file in SafeEnumerateFiles(dir))
        {
            if (matches.Count >= maxMatches)
            {
                truncated = true;
                return;
            }
            var relative = ToRelative(file);
            if (prefixes.Length > 0 && !prefixes.Any(p => PathMatcher.MatchesPrefix(relative, p)))
                continue;
            SearchFile(file, relative, query, maxMatches, matches);
        }
    }

    private void SearchFile(string fullPath, string relative, string query, int cap, List<SearchMatch> matches)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)))
            return;

        long size;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            return;
        }

        if (size == 0 || size > MaxFileSize)
            return;

        if (ContainsNulByte(fullPath))
            return;

        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (Exception)
        {
            return;
        }

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
                lineStarts.Add(i + 1);
        }

        var searchFrom = 0;
        while (matches.Count < cap)
        {
            var idx = content.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                break;

            var lineIndex = FindLineIndex(lineStarts, idx);
            var lineStart = lineStarts[lineIndex];
            var lineEnd = content.IndexOf('\n', idx);
            if (lineEnd < 0)
                lineEnd = content.Length;

            var text = content[lineStart..lineEnd].Trim();
            if (text.Length > MaxDisplayLine)
                text = text[..MaxDisplayLine] + "…";

            matches.Add(new SearchMatch(relative, lineIndex + 1, text));
            searchFrom = idx + Math.Max(query.Length, 1);
        }
    }

    private static int FindLineIndex(List<int> lineStarts, int position)
    {
        var lo = 0;
        var hi = lineStarts.Count - 1;
        var answer = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lineStarts[mid] <= position)
            {
                answer = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return answer;
    }

    private bool DirectoryMatchesScope(string dir, string[] prefixes)
    {
        var relative = ToRelative(dir);
        return prefixes.Any(p => PathMatcher.MatchesPrefix(relative, p) || PathMatcher.MatchesPrefix(p, relative));
    }

    private static string[] NormalizePrefixes(IReadOnlyCollection<string>? prefixes)
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

        return new TreeEntry(ToRelative(fullPath), isDirectory, size);
    }

    private string ToRelative(string fullPath)
        => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

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

    private static IReadOnlyList<string> SafeEnumerateDirectories(string dir)
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

    private static IReadOnlyList<string> SafeEnumerateFiles(string dir)
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

    private Result<string> RunGit(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
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

    public Result<CallerResult> GetCallers(string relativePath, string symbolName, int line)
    {
        if (!Directory.Exists(Root))
            return Result.Failure<CallerResult>("directory not found");
        if (string.IsNullOrWhiteSpace(symbolName))
            return Result.Failure<CallerResult>("missing symbol name");
        if (line < 1)
            return Result.Failure<CallerResult>("line must be >= 1");

        var resolved = WorkspacePath.ResolveInsideRoot(Root, relativePath);
        if (!resolved.Success)
            return Result.Failure<CallerResult>(resolved.Error ?? "invalid path");
        if (!File.Exists(resolved.Value))
            return Result.Failure<CallerResult>("file not found");

        var normalized = relativePath.Replace('\\', '/');
        var language = _registry.Resolve(normalized);

        if (language?.Backend == SignatureBackend.Roslyn)
        {
            var roslynScope = new CallerScope(Root, normalized, symbolName, line, []);
            var roslynResult = new RoslynCallerFinder().Find(roslynScope);
            if (roslynResult.Success)
                return roslynResult;

            var roslynCandidates = CollectCandidateFiles(language.Extensions);
            var heurScope = new CallerScope(Root, normalized, symbolName, line, roslynCandidates);
            var heurResult = new HeuristicCallerFinder().Find(heurScope);
            if (heurResult.Success)
            {
                var v = heurResult.Value!;
                return Result.Ok(new CallerResult(v.Symbol, v.Callers, v.Backend, false));
            }
            return heurResult;
        }

        var extensions = language?.Extensions ?? [Path.GetExtension(normalized)];
        var candidates = CollectCandidateFiles(extensions);
        var scope = new CallerScope(Root, normalized, symbolName, line, candidates);
        return new HeuristicCallerFinder().Find(scope);
    }

    public Result<string> HashRange(string path, int startLine, int endLine)
    {
        if (startLine < 1)
            return Result.Failure<string>("startLine must be >= 1");
        if (endLine < startLine)
            return Result.Failure<string>("endLine must be >= startLine");

        var resolved = WorkspacePath.ResolveInsideRoot(Root, path);
        if (!resolved.Success)
            return Result.Failure<string>(resolved.Error ?? "invalid path");

        var fullPath = resolved.Value;
        if (!File.Exists(fullPath))
            return Result.Failure<string>("file not found");

        long size;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            return Result.Failure<string>("file not found");
        }

        if (size > MaxFileSize)
            return Result.Failure<string>("file too large");
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)) || ContainsNulByte(fullPath))
            return Result.Failure<string>("file is binary");

        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (Exception)
        {
            return Result.Failure<string>("file not found");
        }

        var lines = SplitLines(content);
        if (endLine > lines.Length)
            return Result.Failure<string>($"endLine exceeds file length ({lines.Length} lines)");

        var range = string.Join("\n", lines[(startLine - 1)..endLine]);
        return Result.Ok(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(range))).ToLowerInvariant());
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

    private List<string> CollectCandidateFiles(string[] extensions)
    {
        var files = new List<string>();
        CollectFilesForCallers(Root, extensions, files);
        return files;
    }

    private void CollectFilesForCallers(string dir, string[] extensions, List<string> files)
    {
        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(sub)))
                continue;
            CollectFilesForCallers(sub, extensions, files);
        }

        foreach (var file in SafeEnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (extensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (new FileInfo(file).Length <= MaxFileSize)
                        files.Add(ToRelative(file));
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
