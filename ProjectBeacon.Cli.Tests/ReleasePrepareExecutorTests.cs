namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class ReleasePrepareExecutorTests
{
    [Fact]
    public void ResolveManifestPath_CreatesDirectoryInsideReleaseWorktree()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-release-manifest-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(parent, "worktree");
        Directory.CreateDirectory(worktree);
        try
        {
            var physicalWorktree = AgentTaskWorktree.ResolvePhysicalPath(worktree);
            var result = ReleasePrepareExecutor.ResolveManifestPath(physicalWorktree);

            Assert.Null(result.Error);
            Assert.Equal(Path.Combine(physicalWorktree, ".beacon", "release.json"), result.Path);
            Assert.True(Directory.Exists(Path.Combine(physicalWorktree, ".beacon")));
            Assert.False(File.Exists(result.Path));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveManifestPath_RejectsBeaconDirectorySymlinkOutsideWorktree()
    {
        if (OperatingSystem.IsWindows())
            return;

        var parent = Path.Combine(Path.GetTempPath(), "beacon-release-manifest-dir-link-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(parent, "worktree");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(outside);
        try
        {
            var physicalWorktree = AgentTaskWorktree.ResolvePhysicalPath(worktree);
            var physicalOutside = AgentTaskWorktree.ResolvePhysicalPath(outside);
            Directory.CreateSymbolicLink(Path.Combine(physicalWorktree, ".beacon"), physicalOutside);

            var result = ReleasePrepareExecutor.ResolveManifestPath(physicalWorktree);

            Assert.Null(result.Path);
            Assert.Contains("symbolic link or junction", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(physicalOutside, "release.json")));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveManifestPath_RejectsDanglingManifestFileSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        var parent = Path.Combine(Path.GetTempPath(), "beacon-release-manifest-file-link-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(parent, "worktree");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(Path.Combine(worktree, ".beacon"));
        Directory.CreateDirectory(outside);
        try
        {
            var physicalWorktree = AgentTaskWorktree.ResolvePhysicalPath(worktree);
            var physicalOutside = AgentTaskWorktree.ResolvePhysicalPath(outside);
            var outsideFile = Path.Combine(physicalOutside, "release.json");
            File.CreateSymbolicLink(Path.Combine(physicalWorktree, ".beacon", "release.json"), outsideFile);

            var result = ReleasePrepareExecutor.ResolveManifestPath(physicalWorktree);

            Assert.Null(result.Path);
            Assert.Contains("symbolic link or junction", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outsideFile));
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }
}
