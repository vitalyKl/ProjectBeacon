namespace ProjectBeacon.Infrastructure.Data;

public static class EnvFile
{
    public static void Load()
    {
        foreach (var dir in CandidateDirectories())
        {
            var path = Path.Combine(dir, ".env");
            if (!File.Exists(path))
                continue;
            Apply(path);
            return;
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (seen.Add(dir.FullName))
                    yield return dir.FullName;
            }
        }
    }

    private static void Apply(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].Trim();

            var eq = line.IndexOf('=');
            if (eq < 1)
                continue;

            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());
            if (key.Length == 0)
                continue;
            if (Environment.GetEnvironmentVariable(key) is not null)
                continue;
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
