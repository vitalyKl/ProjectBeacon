namespace ProjectBeacon.Application.Common;

using Domain.Entities.Projects;

public static class AutoLabel
{
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
