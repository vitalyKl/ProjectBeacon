namespace ProjectBeacon.Cli.Tests;

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
            var result = ReleasePrepareExecutor.ResolveManifestPath(worktree);

            Assert.Null(result.Error);
            Assert.Equal(Path.Combine(worktree, ".beacon", "release.json"), result.Path);
            Assert.True(Directory.Exists(Path.Combine(worktree, ".beacon")));
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
            Directory.CreateSymbolicLink(Path.Combine(worktree, ".beacon"), outside);

            var result = ReleasePrepareExecutor.ResolveManifestPath(worktree);

            Assert.Null(result.Path);
            Assert.Contains("symbolic link or junction", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(outside, "release.json")));
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
            var outsideFile = Path.Combine(outside, "release.json");
            File.CreateSymbolicLink(Path.Combine(worktree, ".beacon", "release.json"), outsideFile);

            var result = ReleasePrepareExecutor.ResolveManifestPath(worktree);

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
