namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;

public sealed class IndependentReleaseProjectTests
{
    [Fact]
    public void NpmPackageJson_UsesSemVerAndCustomReleaseTagProjection()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "NpmReleaseProject", "package.json");
        Assert.True(File.Exists(manifestPath), "The independent package.json fixture must be copied to test output.");
        var manifest = File.ReadAllText(manifestPath);

        var policy = new ProjectVersionPolicy(
            "SemVer",
            3,
            null,
            "release-{version}",
            false,
            [new ProjectVersionSourcePolicy("package.json", "Json", "/version")]);

        Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var policyError), policyError);
        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, policy.Sources[0], manifest, out var version, out var extractionError), extractionError);

        Assert.Equal("2.4.1", version);
        Assert.True(ProjectVersionPolicyCatalog.TryNormalizeVersion(
            policy, version, out var normalized, out var versionError), versionError);
        Assert.Equal("2.4.1", normalized);
        Assert.Equal("release-2.4.1", ProjectVersionPolicyCatalog.FormatTag(policy, normalized));
    }
}
