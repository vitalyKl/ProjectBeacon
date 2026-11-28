namespace ProjectBeacon.Cli.Tests;

using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;
using ProjectBeacon.Domain.Entities.Projects;

public sealed class ReleasePrepareExecutorTests
{
    [Fact]
    public async Task ReleasePrepare_WritesIndependentPackageJsonVersionSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-npm-version-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "package.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "name": "beacon-release-policy-proof",
                  "version": "2.4.1",
                  "private": true
                }
                """);
            var source = new ProjectVersionSourcePolicy("package.json", "Json", "/version");
            var policy = new ProjectVersionPolicy(
                "SemVer", 3, null, "release-{version}", false, [source]);

            var result = await ReleasePrepareExecutor.WriteVersionSourceAsync(
                path, source, policy, "2.4.2", TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal("beacon-release-policy-proof", json["name"]!.GetValue<string>());
            Assert.Equal("2.4.2", json["version"]!.GetValue<string>());
            Assert.True(json["private"]!.GetValue<bool>());
            Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
                policy, source, json.ToJsonString(), out var projected, out var error), error);
            Assert.Equal("2.4.2", projected);
            Assert.Equal("release-2.4.2", ProjectVersionPolicyCatalog.FormatTag(policy, projected));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReleasePrepare_WritesJsonDocumentRootVersionSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-json-root-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "version.json");
        try
        {
            await File.WriteAllTextAsync(path, "\"2.4.1\"");
            var source = new ProjectVersionSourcePolicy("version.json", "Json", "");
            var policy = new ProjectVersionPolicy(
                "SemVer", 3, null, "release-{version}", false, [source]);
            Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var policyError), policyError);

            var result = await ReleasePrepareExecutor.WriteVersionSourceAsync(
                path, source, policy, "2.4.2", TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("2.4.2", document!.GetValue<string>());
            Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
                policy, source, document.ToJsonString(), out var projected, out var error), error);
            Assert.Equal("2.4.2", projected);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReleasePrepare_WritesCanonicalJsonArrayIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-json-array-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "versions.json");
        const string original = """{"versions":["2.4.1"]}""";
        try
        {
            await File.WriteAllTextAsync(path, original);
            var source = new ProjectVersionSourcePolicy("versions.json", "Json", "/versions/0");
            var policy = new ProjectVersionPolicy(
                "SemVer", 3, null, "release-{version}", false, [source]);
            Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var policyError), policyError);

            var result = await ReleasePrepareExecutor.WriteVersionSourceAsync(
                path, source, policy, "2.4.2", TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal("2.4.2", document["versions"]![0]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("/versions/00")]
    [InlineData("/versions/+0")]
    [InlineData("/versions/٠")]
    public async Task ReleasePrepare_RejectsNonCanonicalJsonArrayIndexWithoutChangingSource(string selector)
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-json-array-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "versions.json");
        const string original = """{"versions":["2.4.1"]}""";
        try
        {
            await File.WriteAllTextAsync(path, original);
            var source = new ProjectVersionSourcePolicy("versions.json", "Json", selector);
            var policy = new ProjectVersionPolicy(
                "SemVer", 3, null, "release-{version}", false, [source]);
            Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var policyError), policyError);

            var result = await ReleasePrepareExecutor.WriteVersionSourceAsync(
                path, source, policy, "2.4.2", TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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
