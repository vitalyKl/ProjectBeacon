namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Mcp;

// Symlink creation needs a privilege on Windows (developer mode / admin) and is
// always available on Linux; probe once at discovery and skip with a reason when
// the environment cannot create links, instead of failing or silently passing.
public sealed class SymlinkRequiredFactAttribute : FactAttribute
{
    private static readonly bool Supported = ProbeSymbolicLinkSupport();

    public SymlinkRequiredFactAttribute()
    {
        if (!Supported)
            Skip = "symbolic links cannot be created by this user/machine";
    }

    private static bool ProbeSymbolicLinkSupport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-linkprobe-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(dir, "probe");
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateSymbolicLink(link, dir);
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(link))
                    Directory.Delete(link);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // probe cleanup only
            }
        }
    }
}

public sealed class WorkspacePathTests
{
    [Fact]
    public void ResolveInsideRoot_RejectsParentEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = WorkspacePath.ResolveInsideRoot(root, "../secret.txt");
            Assert.False(result.Success);
            Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveInsideRoot_RejectsMissingPath()
    {
        var result = WorkspacePath.ResolveInsideRoot(Path.GetTempPath(), "  ");
        Assert.False(result.Success);
        Assert.Equal("missing path", result.Error);
    }

    [Fact]
    public void WriteFile_InsideRoot_CreatesFile_AndParentEscape_DoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        var outside = Path.GetFullPath(Path.Combine(root, "..", "beacon-outside-" + Guid.NewGuid().ToString("N") + ".txt"));
        var ws = new FileWorkspace(root);
        try
        {
            var ok = ws.WriteFile("src/hello.txt", "hi");
            Assert.True(ok.Success);
            Assert.True(File.Exists(Path.Combine(root, "src", "hello.txt")));

            var escape = ws.WriteFile("../beacon-escape.txt", "nope");
            Assert.False(escape.Success);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            if (File.Exists(outside))
                File.Delete(outside);
        }
    }

    [SymlinkRequiredFact]
    public void ResolveInsideRoot_RejectsDirectoryJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "beacon-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "out");
        try
        {
            Directory.CreateSymbolicLink(link, outside);

            var result = WorkspacePath.ResolveInsideRoot(root, "out/leak.txt");
            Assert.False(result.Success);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [SymlinkRequiredFact]
    public void ResolveInsideRoot_RejectsSymlinkDirectory_Escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "beacon-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var leak = Path.Combine(outside, "leak.txt");
        File.WriteAllText(leak, "sentinel");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);

            var resolved = WorkspacePath.ResolveInsideRoot(root, "link/leak.txt");
            Assert.False(resolved.Success);
            Assert.Contains("escapes", resolved.Error, StringComparison.OrdinalIgnoreCase);

            var ws = new FileWorkspace(root);
            var read = ws.ReadFile("link/leak.txt");
            Assert.False(read.Success);

            var write = ws.WriteFile("link/leak.txt", "pwned");
            Assert.False(write.Success);

            Assert.Equal("sentinel", File.ReadAllText(leak));
            Assert.Single(Directory.GetFiles(outside));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            else if (File.Exists(link))
                File.Delete(link);
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void ApplyPatch_RequiresExactMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        var ws = new FileWorkspace(root);
        try
        {
            Assert.True(ws.WriteFile("a.txt", "alpha beta").Success);
            Assert.False(ws.ApplyPatch("a.txt", "missing", "x").Success);
            Assert.True(ws.ApplyPatch("a.txt", "beta", "gamma").Success);
            Assert.Equal("alpha gamma", ws.ReadFile("a.txt").Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData(@"C:\secret")]
    [InlineData("C:/secret")]
    [InlineData(@"\\server\share\secret")]
    [InlineData("//server/share/secret")]
    public void ResolveInsideRoot_RootedPaths_Rejected_WithRelativeRequired(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = WorkspacePath.ResolveInsideRoot(root, path);
            Assert.False(result.Success);
            Assert.Equal(WorkspacePath.RelativePathRequired, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("src/file.cs")]
    [InlineData(@"src\file.cs")]
    [InlineData("file..name.cs")]
    public void ResolveInsideRoot_ValidRelativePaths_Accepted(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = WorkspacePath.ResolveInsideRoot(root, path);
            Assert.True(result.Success, result.Error);
            Assert.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, result.Value!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("a/../b")]
    [InlineData(@"a\..\b")]
    public void ResolveInsideRoot_Traversal_Rejected(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = WorkspacePath.ResolveInsideRoot(root, path);
            Assert.False(result.Success);
            Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData("C:\\secret")]
    [InlineData("C:/secret")]
    [InlineData("c:\\lower")]
    [InlineData("C:")]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("\\\\server\\share\\secret")]
    [InlineData("//server/share/secret")]
    public void IsRootedPortable_RootedForms_ReturnTrue(string path)
    {
        Assert.True(WorkspacePath.IsRootedPortable(path));
    }

    [Theory]
    [InlineData("src/file.cs")]
    [InlineData("src\\file.cs")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("relative/path")]
    [InlineData("relative\\path")]
    [InlineData("file..name.cs")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("a/../b")]
    [InlineData("a\\..\\b")]
    [InlineData("C:foo")]
    [InlineData(".")]
    [InlineData("")]
    public void IsRootedPortable_RelativeAndTraversal_ReturnFalse(string path)
    {
        Assert.False(WorkspacePath.IsRootedPortable(path));
    }
}
