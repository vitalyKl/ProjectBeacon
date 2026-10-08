namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

/// <summary>
/// Public entry for the local code index. It caches nothing: every query re-scans the working tree.
/// </summary>
public sealed class CodeIndex
{
    public const int DefaultMaxEntries = 2000;
    public const int DefaultMaxMatches = 100;
    public const int DefaultMaxFiles = 10000;

    private readonly CodeWorkspace _files;
    private readonly CodeTree _tree;
    private readonly CodeSearch _search;
    private readonly GitScope _git;
    private readonly SignatureQuery _signatures;
    private readonly CallerQuery _callers;
    private readonly CodeHash _hash;

    public CodeIndex(string root) : this(root, LanguageRegistry.CreateDefault()) { }

    public CodeIndex(string root, ILanguageRegistry registry)
    {
        _files = new CodeWorkspace(root);
        _tree = new CodeTree(_files);
        _search = new CodeSearch(_files);
        _git = new GitScope(_files);
        _signatures = new SignatureQuery(_files, registry);
        _callers = new CallerQuery(_files, registry);
        _hash = new CodeHash(_files);
    }

    public string Root => _files.Root;

    public Result<TreeResult> GetTree(string? subPath = null, int maxEntries = DefaultMaxEntries)
        => _tree.GetTree(subPath, maxEntries);

    public Result<IReadOnlyList<string>> GetChangedFiles()
        => _git.GetChangedFiles();

    public Result<IReadOnlyList<string>> GetChangedScope(IReadOnlyCollection<string>? paths = null, int maxFiles = DefaultMaxFiles)
        => _git.GetChangedScope(paths, maxFiles);

    public Result<SignatureResult> GetSignatures(IReadOnlyCollection<string>? paths = null, int maxFiles = DefaultMaxFiles)
    {
        if (CodeWorkspace.FirstRootedPrefix(paths) is not null)
            return Result.Failure<SignatureResult>(WorkspacePath.RelativePathRequired);

        if (paths is not null && paths.Count == 0)
            return Result.Ok(new SignatureResult([], ""));

        IReadOnlyCollection<string> targetFiles;
        if (paths is null)
        {
            var changed = _git.GetChangedFiles();
            if (!changed.Success)
                return Result.Failure<SignatureResult>(changed.Error ?? "git failed");
            targetFiles = changed.Value!;
        }
        else
        {
            targetFiles = paths;
        }

        return _signatures.Extract(targetFiles, maxFiles);
    }

    public Result<SearchResult> Search(string query, IReadOnlyCollection<string>? paths = null, int maxMatches = DefaultMaxMatches)
        => _search.Search(query, paths, maxMatches);

    public static Result<List<string>> FilterByScope(IReadOnlyCollection<string> files, IReadOnlyCollection<string>? prefixes)
        => GitScope.FilterByScope(files, prefixes);

    public static List<string> ParsePorcelain(string output)
        => GitScope.ParsePorcelain(output);

    public Result<CallerResult> GetCallers(string relativePath, string symbolName, int line)
        => _callers.GetCallers(relativePath, symbolName, line);

    public Result<string> HashRange(string path, int startLine, int endLine)
        => _hash.HashRange(path, startLine, endLine);
}
