namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Mcp;

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
            Assert.Contains("escapes", result.Error, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ResolveInsideRoot_RejectsDirectoryJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-ws-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "beacon-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "out");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception)
            {
                return;
            }

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
}
