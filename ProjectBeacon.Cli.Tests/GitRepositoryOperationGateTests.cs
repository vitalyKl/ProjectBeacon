namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class GitRepositoryOperationGateTests
{
    [Fact]
    public void NormalizeRoot_ResolvesSymlinkedAncestorPaths()
    {
        if (OperatingSystem.IsWindows())
            return;

        if (OperatingSystem.IsMacOS())
        {
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var alternateTempRoot = tempRoot.StartsWith("/private/var/", StringComparison.Ordinal)
                ? tempRoot["/private".Length..]
                : tempRoot.StartsWith("/var/", StringComparison.Ordinal)
                    ? "/private" + tempRoot
                    : null;
            Assert.NotNull(alternateTempRoot);
            Assert.Equal(
                GitRepositoryOperationGate.NormalizeRoot(tempRoot),
                GitRepositoryOperationGate.NormalizeRoot(alternateTempRoot!));
        }

        var parent = Path.Combine(
            Path.GetTempPath(),
            "beacon-git-gate-path-" + Guid.NewGuid().ToString("N"));
        var physicalParent = Path.Combine(parent, "physical");
        var aliasParent = Path.Combine(parent, "alias");
        var physicalWorktree = Path.Combine(physicalParent, "session");
        var aliasWorktree = Path.Combine(aliasParent, "session");

        Directory.CreateDirectory(physicalWorktree);
        try
        {
            Directory.CreateSymbolicLink(aliasParent, physicalParent);

            Assert.Equal(
                GitRepositoryOperationGate.NormalizeRoot(physicalWorktree),
                GitRepositoryOperationGate.NormalizeRoot(aliasWorktree));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
