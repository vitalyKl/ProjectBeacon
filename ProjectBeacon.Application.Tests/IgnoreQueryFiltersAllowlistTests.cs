namespace ProjectBeacon.Application.Tests;

using System.Text.RegularExpressions;

public sealed class IgnoreQueryFiltersAllowlistTests
{
    private static readonly HashSet<string> ProductionProjects = new(StringComparer.Ordinal)
    {
        "ProjectBeacon.API",
        "ProjectBeacon.Application",
        "ProjectBeacon.Cli",
        "ProjectBeacon.Domain",
        "ProjectBeacon.Infrastructure",
        "ProjectBeacon.Web",
        "ProjectBeacon.Worker"
    };

    private static readonly Regex BypassCall = new(@"\bIgnoreQueryFilters\s*\(", RegexOptions.Compiled);

    [Fact]
    public void ProductionTenantFilterBypasses_MatchReviewedPathAndCountAllowlist()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, "docs", "ignore-query-filters.allowlist");
        Assert.True(File.Exists(manifestPath), $"Missing reviewed allowlist: {manifestPath}");

        var expected = File.ReadAllLines(manifestPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(ParseEntry)
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        var actual = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                FullPath = file,
                RelativePath = Path.GetRelativePath(root, file).Replace('\\', '/')
            })
            .Where(file =>
            {
                var segments = file.RelativePath.Split('/');
                return ProductionProjects.Contains(segments[0])
                    && !segments.Any(segment => segment is "bin" or "obj");
            })
            .Select(file => new
            {
                file.RelativePath,
                Count = BypassCall.Matches(File.ReadAllText(file.FullPath)).Count
            })
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.RelativePath, file => file.Count, StringComparer.Ordinal);

        var unapproved = actual.Keys.Except(expected.Keys, StringComparer.Ordinal).OrderBy(path => path).ToArray();
        var stale = expected.Keys.Except(actual.Keys, StringComparer.Ordinal).OrderBy(path => path).ToArray();
        Assert.True(unapproved.Length == 0,
            "New IgnoreQueryFilters() call sites require an explicit reviewed entry:\n" + string.Join("\n", unapproved));
        Assert.True(stale.Length == 0,
            "Allowlist entries no longer used should be removed:\n" + string.Join("\n", stale));

        foreach (var entry in expected.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason), $"{entry.Path} needs a rationale.");
            Assert.Equal(entry.Count, actual[entry.Path]);
        }
    }

    private static (string Path, int Count, string Reason) ParseEntry(string line)
    {
        var parts = line.Split('|', 3);
        Assert.Equal(3, parts.Length);
        Assert.False(string.IsNullOrWhiteSpace(parts[0]));
        Assert.True(int.TryParse(parts[1], out var count) && count > 0,
            $"Invalid IgnoreQueryFilters() count in allowlist entry: {line}");
        Assert.False(string.IsNullOrWhiteSpace(parts[2]));
        return (parts[0], count, parts[2]);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProjectBeacon.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate ProjectBeacon.sln while checking IgnoreQueryFilters() allowlist.");
    }
}
