namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

public sealed record CallerScope(
    string Root,
    string RelativePath,
    string SymbolName,
    int Line,
    IReadOnlyList<string> CandidateFiles);

public interface ICallerFinder
{
    string Id { get; }
    Result<CallerResult> Find(CallerScope scope);
}
