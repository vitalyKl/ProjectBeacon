namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;
/// <summary>
/// Where to look for callers: root, file, symbol, line, and candidate files.
/// </summary>
public sealed record CallerScope(
    string Root,
    string RelativePath,
    string SymbolName,
    int Line,
    IReadOnlyList<string> CandidateFiles);
/// <summary>
/// Finds single-hop callers of a symbol at a file and line.
/// </summary>
public interface ICallerFinder
{
    string Id { get; }
    Result<CallerResult> Find(CallerScope scope);
}
