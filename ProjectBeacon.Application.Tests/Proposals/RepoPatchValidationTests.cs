namespace ProjectBeacon.Application.Tests.Proposals;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Proposals;

public sealed class RepoPatchValidationTests
{
    private static readonly string BaseHash = Hash("before\n");
    private static readonly string ResultHash = Hash("after\n");

    [Fact]
    public void TryValidate_AcceptsExactDocumentationUpdate()
    {
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            index 9d3f98b..f5a8c3d 100644
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1 +1 @@
            -before
            +after
            """;
        var payload = Payload(patch, new { gitPath = "docs/guide.md", projectPath = "docs/guide.md", baseHash = BaseHash, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out var result, out var error);

        Assert.True(valid, error);
        Assert.NotNull(result);
        Assert.Single(result.Files);
        Assert.Equal(BaseHash, result.Files[0].BaseHash);
        Assert.Equal(ResultHash, result.Files[0].ResultHash);
    }

    [Fact]
    public void TryValidate_AcceptsWindowsLineEndings()
    {
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            index 9d3f98b..f5a8c3d 100644
            --- a/docs/guide.md
            +++ b/docs/guide.md
            @@ -1 +1 @@
            -before
            +after
            """.Replace("\n", "\r\n", StringComparison.Ordinal);
        var payload = Payload(patch, new { gitPath = "docs/guide.md", projectPath = "docs/guide.md", baseHash = BaseHash, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out var result, out var error);

        Assert.True(valid, error);
        Assert.NotNull(result);
        Assert.Equal("docs/guide.md", Assert.Single(result.Files).GitPath);
    }

    [Fact]
    public void TryValidate_AcceptsNewDocumentationFileOnlyWithDevNullOldHeader()
    {
        var patch = """
            diff --git a/docs/new.md b/docs/new.md
            new file mode 100644
            --- /dev/null
            +++ b/docs/new.md
            @@ -0,0 +1 @@
            +after
            """;
        var payload = Payload(patch, new { gitPath = "docs/new.md", projectPath = "docs/new.md", baseHash = (string?)null, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out _, out var error);

        Assert.True(valid, error);
    }

    [Fact]
    public void TryValidate_AcceptsDeletionOnlyWithDevNullNewHeader()
    {
        var patch = """
            diff --git a/docs/old.md b/docs/old.md
            deleted file mode 100644
            --- a/docs/old.md
            +++ /dev/null
            @@ -1 +0,0 @@
            -before
            """;
        var payload = Payload(patch, new { gitPath = "docs/old.md", projectPath = "docs/old.md", baseHash = BaseHash, resultHash = (string?)null });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out _, out var error);

        Assert.True(valid, error);
    }

    [Theory]
    [InlineData("+++ b/../../outside.md")]
    [InlineData("+++ b/src/guide.md")]
    [InlineData("+++ /absolute/guide.md")]
    public void TryValidate_RejectsUnifiedNewPathThatDoesNotMatchDiffManifest(string newHeader)
    {
        var patch = $"""
            diff --git a/docs/guide.md b/docs/guide.md
            --- a/docs/guide.md
            {newHeader}
            @@ -1 +1 @@
            -before
            +after
            """;
        var payload = Payload(patch, new { gitPath = "docs/guide.md", projectPath = "docs/guide.md", baseHash = BaseHash, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out _, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_RejectsIncompleteUnifiedHeaders()
    {
        var patch = """
            diff --git a/docs/guide.md b/docs/guide.md
            --- a/docs/guide.md
            @@ -1 +1 @@
            -before
            +after
            """;
        var payload = Payload(patch, new { gitPath = "docs/guide.md", projectPath = "docs/guide.md", baseHash = BaseHash, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out _, out _);

        Assert.False(valid);
    }

    [Fact]
    public void TryValidate_RejectsCreateManifestWhenPatchDoesNotUseDevNull()
    {
        var patch = """
            diff --git a/docs/new.md b/docs/new.md
            --- a/docs/new.md
            +++ b/docs/new.md
            @@ -0,0 +1 @@
            +after
            """;
        var payload = Payload(patch, new { gitPath = "docs/new.md", projectPath = "docs/new.md", baseHash = (string?)null, resultHash = ResultHash });

        var valid = RepoPatchValidation.TryValidate(
            payload, ["docs"], ["docs", "README.md"], out _, out _);

        Assert.False(valid);
    }

    private static string Payload(string patch, object file) =>
        JsonSerializer.Serialize(new { patch, files = new[] { file } });

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
