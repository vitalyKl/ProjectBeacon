namespace ProjectBeacon.Application.Common;

using Domain.Entities.Projects;

/// <summary>Selects the label whose matching path prefix is the longest.</summary>
public static class AutoLabel
{
    /// <summary>The label with the longest matching prefix, or null when none match.</summary>
    public static Label? Match(IEnumerable<Label> labels, string path)
    {
        return labels
            .Select(label => (label, prefix: BestPrefix(label, path)))
            .Where(x => x.prefix is not null)
            .OrderByDescending(x => x.prefix!.Length)
            .Select(x => x.label)
            .FirstOrDefault();
    }

    private static string? BestPrefix(Label label, string path)
    {
        return label.AllPrefixes()
            .Where(prefix => PathMatcher.MatchesPrefix(path, prefix))
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault();
    }
}
