namespace ProjectBeacon.Cli.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.CodeIndex;
using ProjectBeacon.Cli.Mcp;

public sealed class BriefPatcherTests
{
    [Fact]
    public void FormatTree_ReturnsFormattedOutput()
    {
        var tree = new TreeResult("/root",
        [
            new TreeEntry("src/", true, 0),
            new TreeEntry("src/Main.cs", false, 1024),
            new TreeEntry("README.md", false, 512)
        ], false);

        var result = BriefPatcher.FormatTree(tree);

        Assert.Contains("# Tree (3 entries)", result);
        Assert.Contains("src/", result);
        Assert.Contains("Main.cs", result);
        Assert.Contains("README.md", result);
    }

    [Fact]
    public void FormatTree_Truncated_Suffix()
    {
        var tree = new TreeResult("/root",
        [
            new TreeEntry("a.txt", false, 100)
        ], true);

        var result = BriefPatcher.FormatTree(tree);

        Assert.Contains("truncated", result);
    }

    [Fact]
    public void FormatFileList_Empty_ReturnsNoChanged()
    {
        var result = BriefPatcher.FormatFileList([]);
        Assert.Equal("No changed files.", result);
    }

    [Fact]
    public void FormatFileList_ReturnsNewlineSeparated()
    {
        var files = new List<string> { "a.cs", "b.cs", "c.cs" };
        var result = BriefPatcher.FormatFileList(files);

        Assert.Contains("a.cs", result);
        Assert.Contains("b.cs", result);
        Assert.Contains("c.cs", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void ExtractBriefMarkdown_FromResultEnvelope()
    {
        var json = new JsonObject
        {
            ["success"] = true,
            ["value"] = new JsonObject
            {
                ["briefMarkdown"] = "# Project\n\n## Goals\n\nDone."
            }
        };
        var serialized = json.ToJsonString();
        var extracted = BriefPatcher.ExtractBriefMarkdown(serialized);

        Assert.Equal("# Project\n\n## Goals\n\nDone.", extracted);
    }

    [Fact]
    public void ExtractBriefMarkdown_FromSimpleStructure()
    {
        var json = new JsonObject
        {
            ["briefMarkdown"] = "# Project\n\n## Goals\n\nDone."
        };
        var serialized = json.ToJsonString();
        var extracted = BriefPatcher.ExtractBriefMarkdown(serialized);

        Assert.Equal("# Project\n\n## Goals\n\nDone.", extracted);
    }

    [Fact]
    public void ExtractBriefMarkdown_NestedValue()
    {
        var json = new JsonObject
        {
            ["success"] = true,
            ["value"] = new JsonObject
            {
                ["value"] = new JsonObject
                {
                    ["briefMarkdown"] = "# Project"
                }
            }
        };
        var serialized = json.ToJsonString();
        var extracted = BriefPatcher.ExtractBriefMarkdown(serialized);

        Assert.Equal("# Project", extracted);
    }

    [Fact]
    public void ExtractBriefMarkdown_NoBriefField_ReturnsNull()
    {
        var json = new JsonObject { ["foo"] = "bar" };
        var serialized = json.ToJsonString();
        var extracted = BriefPatcher.ExtractBriefMarkdown(serialized);

        Assert.Null(extracted);
    }

    [Fact]
    public void PatchBriefMarkdown_ReplacesTreeSection()
    {
        var brief = "# Project\n\n## Tree\n\nstub tree\n\n## Goals\n\nDone.";
        var index = new CodeIndex(Path.GetTempPath());

        var patched = BriefPatcher.PatchBriefMarkdown(brief, index);

        Assert.DoesNotContain("stub tree", patched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# Tree", patched);
        // Goals section should still be there
        Assert.Contains("## Goals", patched);
    }

    [Fact]
    public void PatchBriefMarkdown_FallbackWhenNotGit()
    {
        var nonGitRoot = Path.Combine(Path.GetTempPath(), "beacon-notgit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(nonGitRoot);
            var brief = "# Project\n\n## Tree\n\nstub tree\n\n## Changed scope\n\nstub scope\n\n## Goals\n\nDone.";
            var index = new CodeIndex(nonGitRoot);

            var patched = BriefPatcher.PatchBriefMarkdown(brief, index);

            // Tree section gets replaced (GetTree works on any dir), Changed scope stays as stub (needs git)
            Assert.DoesNotContain("stub tree", patched, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("# Tree", patched);
            Assert.Contains("stub scope", patched, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(nonGitRoot))
                Directory.Delete(nonGitRoot, true);
        }
    }

    [Fact]
    public void PatchBriefMarkdown_NoStubSections_ReturnsOriginal()
    {
        var brief = "# Project\n\n## Goals\n\nDone.";
        var index = new CodeIndex(Path.GetTempPath());

        var patched = BriefPatcher.PatchBriefMarkdown(brief, index);

        Assert.Equal(brief, patched);
    }

    [Fact]
    public void PatchBriefMarkdown_ReplacesBothSections_InGitRepo()
    {
        var gitRoot = TempGitRoot();
        try
        {
            File.WriteAllText(Path.Combine(gitRoot, "README.md"), "# Hello");
            var brief = "# Project\n\n## Tree\n\nstub tree\n\n## Changed scope\n\nstub scope\n\n## Goals\n\nDone.";
            var index = new CodeIndex(gitRoot);

            var patched = BriefPatcher.PatchBriefMarkdown(brief, index);

            Assert.DoesNotContain("stub tree", patched, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stub scope", patched, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("# Tree", patched);
            Assert.Contains("README.md", patched);
        }
        finally
        {
            Directory.Delete(gitRoot, true);
        }
    }

    private static string TempGitRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-test-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit();
        return root;
    }
}
