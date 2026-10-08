namespace ProjectBeacon.Application.CodeIndex;

using System.Text.RegularExpressions;
using Application.Common;
/// <summary>
/// Finds callers by a word-boundary regex over candidate files. This is the fallback when Roslyn cannot build a solution.
/// </summary>
public sealed class HeuristicCallerFinder : ICallerFinder
{
    private const int MaxSnippetLength = 200;

    public string Id => "heuristic";

    public Result<CallerResult> Find(CallerScope scope)
    {
        var callers = new List<CallerSite>();
        var pattern = new Regex($@"\b{Regex.Escape(scope.SymbolName)}\b", RegexOptions.Compiled);

        foreach (var candidate in scope.CandidateFiles)
        {
            var fullPath = Path.Combine(scope.Root, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath);
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                if (candidate == scope.RelativePath && lineNo == scope.Line)
                    continue;
                if (!pattern.IsMatch(lines[i]))
                    continue;

                var trimmed = lines[i].Trim();
                if (trimmed.Length > MaxSnippetLength)
                    trimmed = trimmed[..MaxSnippetLength] + "…";

                var enclosing = FindEnclosingSymbol(lines, i, scope.SymbolName);
                callers.Add(new CallerSite(candidate, lineNo, trimmed, enclosing));
            }
        }

        return Result.Ok(new CallerResult(scope.SymbolName, callers, Id, null));
    }

    private static string? FindEnclosingSymbol(string[] lines, int lineIndex, string symbolName)
    {
        for (var i = lineIndex; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line is "}" or "]" or ")]")
                continue;

            var match = Regex.Match(line, @"^[\w?<>\[\],\s]+\s+(\w+)\s*\(");
            if (match.Success && match.Groups[1].Value != symbolName)
                return match.Groups[1].Value;
        }
        return null;
    }
}
