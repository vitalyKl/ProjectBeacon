namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

internal sealed class SignatureQuery
{
    private readonly CodeWorkspace _files;
    private readonly ILanguageRegistry _registry;
    private readonly ISignatureBackend _roslyn = new RoslynSignatureBackend();
    private readonly ISignatureBackend _treeSitter = new TreeSitterSignatureBackend();

    public SignatureQuery(CodeWorkspace files, ILanguageRegistry registry)
    {
        _files = files;
        _registry = registry;
    }

    public Result<SignatureResult> Extract(IReadOnlyCollection<string> targetFiles, int maxFiles)
    {
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

            var backend = BackendFor(language.Backend);
            var id = backend.Id;
            var resolved = WorkspacePath.ResolveInsideRoot(_files.Root, file);
            if (!resolved.Success)
            {
                results.Add(new FileSignatures(file, [], id, resolved.Error ?? "invalid path"));
                continue;
            }

            var read = _files.TryReadTextFile(resolved.Value!, CodeWorkspace.MaxFileSize);
            if (read.Error is { } error)
            {
                results.Add(new FileSignatures(file, [], id, error switch
                {
                    FileReadError.TooLarge => "file too large",
                    FileReadError.Binary => "file is binary",
                    FileReadError.Unreadable => "unreadable file",
                    _ => "file not found"
                }));
                continue;
            }

            try
            {
                results.Add(backend.Extract(language, file, read.Text!));
            }
            catch (Exception ex)
            {
                results.Add(new FileSignatures(file, [], id, $"extraction failed: {ex.Message}"));
            }
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
}
