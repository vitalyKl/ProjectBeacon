namespace ProjectBeacon.Application.Tests;

using System.Diagnostics;
using Application.CodeIndex;

public sealed class CodeIndexTests
{
    private static string MakeWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        Directory.CreateDirectory(Path.Combine(root, "src", "components"));
        Directory.CreateDirectory(Path.Combine(root, "src2"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "junk"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));

        File.WriteAllText(Path.Combine(root, "src", "app.cs"), "line one\nHello Beacon\nthird\n");
        File.WriteAllText(Path.Combine(root, "src", "components", "widget.cs"), "hello beacon again\n");
        File.WriteAllText(Path.Combine(root, "src2", "other.cs"), "Hello Beacon in src2\n");
        File.WriteAllText(Path.Combine(root, "node_modules", "junk", "skip.js"), "Hello Beacon vendored\n");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "Hello Beacon git\n");
        File.WriteAllText(Path.Combine(root, "bin", "skip.dll"), "Hello Beacon binary\n");
        File.WriteAllBytes(Path.Combine(root, "nul.dat"), new byte[] { (byte)'H', 0x00, (byte)'i' });
        File.WriteAllText(Path.Combine(root, "README.md"), "read me\n");

        return root;
    }

    [Fact]
    public void GetTree_ReturnsRelativePosixPaths_SkipsIgnoredDirectories()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetTree();
            Assert.True(result.Success);
            var entries = result.Value!.Entries;
            var paths = entries.Select(e => e.Path).ToList();

            Assert.Contains("src", paths);
            Assert.Contains("src/app.cs", paths);
            Assert.Contains("src/components", paths);
            Assert.Contains("src/components/widget.cs", paths);
            Assert.Contains("src2/other.cs", paths);
            Assert.Contains("README.md", paths);
            Assert.DoesNotContain("node_modules", paths);
            Assert.DoesNotContain(".git", paths);
            Assert.DoesNotContain("bin", paths);
            Assert.DoesNotContain("node_modules/junk/skip.js", paths);

            var dir = entries.Single(e => e.Path == "src");
            Assert.True(dir.IsDirectory);
            var file = entries.Single(e => e.Path == "README.md");
            Assert.False(file.IsDirectory);
            Assert.True(file.Size > 0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetTree_SubPath_ListsRelativeToRoot_AndRejectsEscape()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);

            var sub = index.GetTree("src");
            Assert.True(sub.Success);
            var paths = sub.Value!.Entries.Select(e => e.Path).ToList();
            Assert.Contains("src/app.cs", paths);
            Assert.Contains("src/components/widget.cs", paths);
            Assert.DoesNotContain("README.md", paths);

            var escape = index.GetTree("../");
            Assert.False(escape.Success);

            var missing = index.GetTree("missing");
            Assert.False(missing.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetTree_TruncatesAtMaxEntries()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetTree(maxEntries: 3);
            Assert.True(result.Success);
            Assert.Equal(3, result.Value!.Entries.Count);
            Assert.True(result.Value.Truncated);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Search_FindsMatches_CaseInsensitive_WithLineNumbers()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).Search("hello beacon");
            Assert.True(result.Success);
            var matches = result.Value!.Matches;
            Assert.False(result.Value.Truncated);
            Assert.Equal(3, matches.Count);

            var paths = matches.Select(m => m.Path).ToList();
            Assert.Contains("src/app.cs", paths);
            Assert.Contains("src/components/widget.cs", paths);
            Assert.Contains("src2/other.cs", paths);

            var app = matches.Single(m => m.Path == "src/app.cs");
            Assert.Equal(2, app.Line);
            Assert.Equal("Hello Beacon", app.Text);

            Assert.DoesNotContain("node_modules/junk/skip.js", paths);
            Assert.DoesNotContain(".git/config", paths);
            Assert.DoesNotContain("nul.dat", paths);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Search_RespectsPathPrefixBoundaries()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).Search("hello beacon", new[] { "src" });
            Assert.True(result.Success);
            var paths = result.Value!.Matches.Select(m => m.Path).ToList();
            Assert.Contains("src/app.cs", paths);
            Assert.Contains("src/components/widget.cs", paths);
            Assert.DoesNotContain("src2/other.cs", paths);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Search_RespectsMaxMatches_AndRejectsEmptyQuery()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);

            var limited = index.Search("hello beacon", null, maxMatches: 2);
            Assert.True(limited.Success);
            Assert.Equal(2, limited.Value!.Matches.Count);
            Assert.True(limited.Value.Truncated);

            var empty = index.Search("  ");
            Assert.False(empty.Success);

            var none = index.Search("no-such-token-xyz");
            Assert.True(none.Success);
            Assert.Empty(none.Value!.Matches);
            Assert.False(none.Value.Truncated);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParsePorcelain_HandlesRenames_QuotedPaths_AndWindowsSlashes()
    {
        var output = "M  src/changed.cs\n"
            + "?? new file.txt\n"
            + "R  old.txt -> renamed.txt\n"
            + "M  \"quoted file.txt\"\n"
            + "M  src\\win\\slash.cs\n";

        var files = CodeIndex.ParsePorcelain(output);
        Assert.Equal(
            new[] { "src/changed.cs", "new file.txt", "renamed.txt", "quoted file.txt", "src/win/slash.cs" },
            files);
    }

    [Fact]
    public void FilterByScope_UsesPathBoundaries_AndDefaultsToAll()
    {
        var files = new[] { "src/app.cs", "src2/other.cs", "README.md" };

        Assert.Equal(new[] { "src/app.cs" }, CodeIndex.FilterByScope(files, new[] { "src" }).Value);
        Assert.Equal(files, CodeIndex.FilterByScope(files, null).Value);
        Assert.Equal(new[] { "src/app.cs", "README.md" }, CodeIndex.FilterByScope(files, new[] { "src", "README.md" }).Value);
    }

    [Fact]
    public void GetChangedFiles_OutsideGitRepository_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetChangedFiles();
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetChangedFiles_ListsModifiedAndUntracked()
    {
        if (!GitAvailable())
            return;

        var root = Path.Combine(Path.GetTempPath(), "beacon-index-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Git(root, "init");
            File.WriteAllText(Path.Combine(root, "a.txt"), "one");
            Git(root, "add", "-A");
            Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init");
            File.WriteAllText(Path.Combine(root, "a.txt"), "two");
            File.WriteAllText(Path.Combine(root, "b.txt"), "three");

            var result = new CodeIndex(root).GetChangedFiles();
            Assert.True(result.Success);
            Assert.Equal(new[] { "a.txt", "b.txt" }, result.Value!.OrderBy(f => f).ToList());
        }
        finally
        {
            DeleteWithRetry(root);
        }
    }

    [Fact]
    public void HashRange_SingleLine_ReturnsBareLowercaseHex()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var result = index.HashRange("src/app.cs", 1, 1);
            Assert.True(result.Success, result.Error);
            var hash = result.Value!;
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
            var expected = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("line one"))).ToLowerInvariant();
            Assert.Equal(expected, hash);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_MultiLine_JoinsWithNewline()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var result = index.HashRange("src/app.cs", 1, 3);
            Assert.True(result.Success, result.Error);
            var expected = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("line one\nHello Beacon\nthird"))).ToLowerInvariant();
            Assert.Equal(expected, result.Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_Deterministic_SameRangeSameHash()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var first = index.HashRange("src/app.cs", 1, 2);
            var second = index.HashRange("src/app.cs", 1, 2);
            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(first.Value, second.Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_DifferentRanges_DifferentHashes()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var a = index.HashRange("src/app.cs", 1, 2);
            var b = index.HashRange("src/app.cs", 2, 3);
            Assert.True(a.Success);
            Assert.True(b.Success);
            Assert.NotEqual(a.Value, b.Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_StartLineLessThanOne_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("src/app.cs", 0, 1);
            Assert.False(result.Success);
            Assert.Contains("startLine", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_EndLineBeforeStartLine_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("src/app.cs", 3, 1);
            Assert.False(result.Success);
            Assert.Contains("endLine", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_EndLineExceedsFileLength_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("src/app.cs", 1, 999);
            Assert.False(result.Success);
            Assert.Contains("exceeds", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_PathEscape_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("../outside.txt", 1, 1);
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_MissingFile_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("nope.txt", 1, 1);
            Assert.False(result.Success);
            Assert.Contains("file not found", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_BinaryFile_Fails()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("nul.dat", 1, 1);
            Assert.False(result.Success);
            Assert.Contains("binary", result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_ContentChange_ChangesHash()
    {
        var root = MakeWorkspace();
        try
        {
            var index = new CodeIndex(root);
            var before = index.HashRange("src/app.cs", 1, 3);
            Assert.True(before.Success);

            File.WriteAllText(Path.Combine(root, "src", "app.cs"), "line one\nHello Beacon\nMODIFIED\n");
            var after = index.HashRange("src/app.cs", 1, 3);
            Assert.True(after.Success);
            Assert.NotEqual(before.Value, after.Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── Path regression: absolute-path behavior per API ───────────────────

    [Theory]
    [InlineData("/absolute/path")]
    [InlineData(@"C:\secret")]
    [InlineData("C:/secret")]
    public void GetTree_AbsoluteSubPath_IsBlocked(string subPath)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetTree(subPath);
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
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
    public void HashRange_AbsolutePath_IsBlocked(string path)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange(path, 1, 1);
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
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
    public void GetCallers_AbsolutePath_IsBlocked(string path)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetCallers(path, "Foo", 1);
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/path.cs")]
    [InlineData(@"C:\secret\file.cs")]
    [InlineData("C:/secret/file.cs")]
    public void GetSignatures_AbsolutePath_ReturnsFailure(string path)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetSignatures(new[] { path });
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/prefix")]
    [InlineData(@"C:\secret")]
    [InlineData("C:/secret")]
    public void Search_AbsolutePrefix_ReturnsFailure(string prefix)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).Search("hello beacon", new[] { prefix });
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/prefix")]
    [InlineData(@"C:\secret")]
    [InlineData("C:/secret")]
    public void GetChangedScope_AbsolutePrefix_ReturnsFailure(string prefix)
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetChangedScope(new[] { prefix });
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/absolute/prefix")]
    [InlineData(@"C:\secret")]
    [InlineData("C:/secret")]
    public void FilterByScope_AbsolutePrefix_Fails(string prefix)
    {
        var files = new[] { "src/app.cs", "src2/other.cs", "README.md" };
        // A rooted prefix is rejected instead of silently matching nothing.
        var result = CodeIndex.FilterByScope(files, new[] { prefix });
        Assert.False(result.Success);
        Assert.Equal(Application.Common.WorkspacePath.RelativePathRequired, result.Error);
    }

    // ── Path regression: traversal still blocked ──────────────────────────

    [Fact]
    public void GetTree_TraversalSubPath_IsBlocked()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetTree("../outside");
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.ParentSegmentNotAllowed, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HashRange_TraversalPath_IsBlocked()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).HashRange("../outside.txt", 1, 1);
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.ParentSegmentNotAllowed, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCallers_TraversalPath_IsBlocked()
    {
        var root = MakeWorkspace();
        try
        {
            var result = new CodeIndex(root).GetCallers("../outside.cs", "Foo", 1);
            Assert.False(result.Success);
            Assert.Equal(Application.Common.WorkspacePath.ParentSegmentNotAllowed, result.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    static CodeIndexTests()
        => SweepStaleWorkspaces();

    // A freshly written git loose object can stay locked by the AV minifilter
    // for minutes after git exits, so a run's leftover gets removed by the
    // next run's sweep instead of blocking the current one.
    private static void SweepStaleWorkspaces()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "beacon-index-git-*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    // Best-effort: delete files first, then directories bottom-up, and leave
    // anything still locked rather than fail an otherwise-passing test.
    private static void DeleteWithRetry(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList())
                DeleteFileWithRetry(file);
        }
        catch { }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                         .OrderBy(d => d.Length).Reverse().ToList())
                DeleteDirWithRetry(dir);

            DeleteDirWithRetry(path);
        }
        catch { }
    }

    private static void DeleteFileWithRetry(string file)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                File.Delete(file);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void DeleteDirWithRetry(string dir)
    {
        try { Directory.Delete(dir); }
        catch { }
    }

    private static bool GitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        _ = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
