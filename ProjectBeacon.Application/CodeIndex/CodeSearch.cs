namespace ProjectBeacon.Application.CodeIndex;

using Application.Common;

internal sealed class CodeSearch
{
    private readonly CodeWorkspace _files;

    public CodeSearch(CodeWorkspace files) => _files = files;

    public Result<SearchResult> Search(string query, IReadOnlyCollection<string>? paths, int maxMatches)
    {
        if (!Directory.Exists(_files.Root))
            return Result.Failure<SearchResult>("directory not found");

        if (string.IsNullOrWhiteSpace(query))
            return Result.Failure<SearchResult>("missing query");

        if (maxMatches <= 0)
            maxMatches = CodeIndex.DefaultMaxMatches;

        if (CodeWorkspace.FirstRootedPrefix(paths) is not null)
            return Result.Failure<SearchResult>(WorkspacePath.RelativePathRequired);

        var prefixes = CodeWorkspace.NormalizePrefixes(paths);
        var matches = new List<SearchMatch>();
        var earlyStop = false;
        SearchDirectory(_files.Root, prefixes, query, maxMatches + 1, matches, ref earlyStop);
        var truncated = matches.Count > maxMatches;
        if (truncated)
            matches.RemoveRange(maxMatches, matches.Count - maxMatches);
        return Result.Ok(new SearchResult(query, matches, truncated));
    }

    private void SearchDirectory(
        string dir,
        string[] prefixes,
        string query,
        int maxMatches,
        List<SearchMatch> matches,
        ref bool truncated)
    {
        foreach (var sub in _files.EnumerateDirectories(dir))
        {
            if (_files.IsIgnoredDirectory(sub))
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

        foreach (var file in _files.EnumerateFiles(dir))
        {
            if (matches.Count >= maxMatches)
            {
                truncated = true;
                return;
            }
            var relative = _files.ToRelative(file);
            if (prefixes.Length > 0 && !prefixes.Any(p => PathMatcher.MatchesPrefix(relative, p)))
                continue;
            SearchFile(file, relative, query, maxMatches, matches);
        }
    }

    private bool DirectoryMatchesScope(string dir, string[] prefixes)
    {
        var relative = _files.ToRelative(dir);
        return prefixes.Any(p => PathMatcher.MatchesPrefix(relative, p) || PathMatcher.MatchesPrefix(p, relative));
    }

    private void SearchFile(string fullPath, string relative, string query, int cap, List<SearchMatch> matches)
    {
        var read = _files.TryReadTextFile(fullPath, CodeWorkspace.MaxFileSize);
        if (read.Error is not null)
            return;

        var content = read.Text!;
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
            if (text.Length > CodeWorkspace.MaxDisplayLine)
                text = text[..CodeWorkspace.MaxDisplayLine] + "…";

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
}
