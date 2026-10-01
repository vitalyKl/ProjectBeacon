namespace ProjectBeacon.Cli.Tests;

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
}
