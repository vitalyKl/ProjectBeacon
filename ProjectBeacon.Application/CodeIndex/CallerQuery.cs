namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

internal sealed class CallerQuery
{
    private readonly CodeWorkspace _files;
    private readonly ILanguageRegistry _registry;

    public CallerQuery(CodeWorkspace files, ILanguageRegistry registry)
    {
        _files = files;
        _registry = registry;
    }

    public Result<CallerResult> GetCallers(string relativePath, string symbolName, int line)
    {
        if (!Directory.Exists(_files.Root))
            return Result.Failure<CallerResult>("directory not found");
        if (string.IsNullOrWhiteSpace(symbolName))
            return Result.Failure<CallerResult>("missing symbol name");
        if (line < 1)
            return Result.Failure<CallerResult>("line must be >= 1");

        var resolved = WorkspacePath.ResolveInsideRoot(_files.Root, relativePath);
        if (!resolved.Success)
            return Result.Failure<CallerResult>(resolved.Error ?? "invalid path");
        if (!File.Exists(resolved.Value))
            return Result.Failure<CallerResult>("file not found");

        var normalized = relativePath.Replace('\\', '/');
        var language = _registry.Resolve(normalized);

        if (language?.Backend == SignatureBackend.Roslyn)
        {
            var roslynScope = new CallerScope(_files.Root, normalized, symbolName, line, []);
            var roslynResult = new RoslynCallerFinder().Find(roslynScope);
            if (roslynResult.Success)
                return roslynResult;

            var roslynCandidates = CollectCandidateFiles(language.Extensions);
            var heurScope = new CallerScope(_files.Root, normalized, symbolName, line, roslynCandidates);
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
        var scope = new CallerScope(_files.Root, normalized, symbolName, line, candidates);
        return new HeuristicCallerFinder().Find(scope);
    }

    private List<string> CollectCandidateFiles(string[] extensions)
    {
        var files = new List<string>();
        Collect(files, _files.Root, extensions);
        return files;
    }

    private void Collect(List<string> files, string dir, string[] extensions)
    {
        foreach (var sub in _files.EnumerateDirectories(dir))
        {
            if (_files.IsIgnoredDirectory(sub))
                continue;
            Collect(files, sub, extensions);
        }

        foreach (var file in _files.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (!extensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (new FileInfo(file).Length <= CodeWorkspace.MaxFileSize)
                    files.Add(_files.ToRelative(file));
            }
            catch (IOException)
            {
            }
        }
    }
}
