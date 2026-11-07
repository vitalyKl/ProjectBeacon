namespace ProjectBeacon.Cli.Tests;

internal static class ProtocolFixtureFiles
{
    public static string Read(string relativePath)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "ProjectBeacon.Cli.Tests", "Fixtures",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(path);
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
            "Could not locate ProjectBeacon.sln while loading protocol fixtures.");
    }
}
