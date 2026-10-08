namespace ProjectBeacon.Application.CodeIndex;

/// <summary>
/// One file or directory in a tree listing.
/// </summary>
public sealed record TreeEntry(string Path, bool IsDirectory, long Size);
/// <summary>
/// Tree listing. Truncated is true when the entry cap was hit.
/// </summary>
public sealed record TreeResult(string Root, IReadOnlyList<TreeEntry> Entries, bool Truncated);
/// <summary>
/// One literal search hit: path, 1-based line, and the line text.
/// </summary>
public sealed record SearchMatch(string Path, int Line, string Text);
/// <summary>
/// Search hits for a query. Truncated is true when the match cap was hit.
/// </summary>
public sealed record SearchResult(string Query, IReadOnlyList<SearchMatch> Matches, bool Truncated);
/// <summary>
/// One extracted symbol: name, kind, signature text, line, and an optional doc comment.
/// </summary>
public sealed record SymbolSignature(string Name, string Kind, string Signature, int Line, string? DocComment);
/// <summary>
/// Signatures from one file, the backend that extracted them, and an error when extraction failed.
/// </summary>
public sealed record FileSignatures(string Path, IReadOnlyList<SymbolSignature> Symbols, string Backend, string? Error);
/// <summary>
/// Signatures across files and the backend name.
/// </summary>
public sealed record SignatureResult(IReadOnlyList<FileSignatures> Files, string Backend);
/// <summary>
/// One incoming reference: path, line, snippet, and the enclosing symbol when known.
/// </summary>
public sealed record CallerSite(string Path, int Line, string Snippet, string? Symbol);
/// <summary>
/// Callers of a symbol. SolutionBuilds is set only for the Roslyn backend.
/// </summary>
public sealed record CallerResult(string Symbol, IReadOnlyList<CallerSite> Callers, string Backend, bool? SolutionBuilds);