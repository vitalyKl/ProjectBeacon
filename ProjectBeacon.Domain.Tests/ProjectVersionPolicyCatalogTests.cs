namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;

public sealed class ProjectVersionPolicyCatalogTests
{
    [Fact]
    public void NumericComponents_AcceptsFourComponentProjectVersion()
    {
        var policy = new ProjectVersionPolicy(
            "NumericComponents", 4, null, "v{version}", false, []);

        Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var error), error);
        Assert.True(ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, "0.7.5.8", out var normalized, out error), error);
        Assert.Equal("0.7.5.8", normalized);
        Assert.False(ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, "0.7.5", out _, out _));
    }

    [Fact]
    public void SemVer_RequiresPrereleaseToBeExplicitlyEnabled()
    {
        var strict = new ProjectVersionPolicy("SemVer", 3, null, "v{version}", false, []);
        var prerelease = strict with { AllowPrerelease = true };

        Assert.True(ProjectVersionPolicyCatalog.TryNormalizeVersion(strict, "1.2.3", out _, out _));
        Assert.False(ProjectVersionPolicyCatalog.TryNormalizeVersion(strict, "1.2.3-rc.1", out _, out _));
        Assert.True(ProjectVersionPolicyCatalog.TryNormalizeVersion(prerelease, "1.2.3-rc.1", out _, out _));
    }

    [Fact]
    public void Calendar_RequiresExactInvariantFormat()
    {
        var policy = new ProjectVersionPolicy("Calendar", 0, "yyyy.MM.dd", "release-{version}", false, []);

        Assert.True(ProjectVersionPolicyCatalog.Validate(policy, out var error), error);
        Assert.True(ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, "2026.11.24", out _, out _));
        Assert.False(ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, "24.11.2026", out _, out _));
    }

    [Theory]
    [InlineData("Xml", "/Project/PropertyGroup/Version", "<Project><PropertyGroup><Version>0.7.5.8</Version></PropertyGroup></Project>")]
    [InlineData("Json", "/version", "{\"version\":\"0.7.5.8\"}")]
    [InlineData("Toml", "package.version", "[package]\nversion = \"0.7.5.8\"")]
    [InlineData("Regex", "version=(?<version>[0-9.]+)", "version=0.7.5.8")]
    public void ExtractVersion_SupportsConfiguredFormats(string format, string selector, string content)
    {
        var policy = new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, []);
        var source = new ProjectVersionSourcePolicy("manifest", format, selector);

        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, content, out var version, out var error), error);
        Assert.Equal("0.7.5.8", version);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/secret.txt")]
    [InlineData("src\\\\outside.json")]
    [InlineData("src/../outside.json")]
    public void Validate_RejectsVersionSourcesOutsideProjectRoot(string path)
    {
        var policy = new ProjectVersionPolicy(
            "SemVer", 3, null, "v{version}", false,
            [new ProjectVersionSourcePolicy(path, "Json", "/version")]);

        Assert.False(ProjectVersionPolicyCatalog.Validate(policy, out _));
    }

    [Fact]
    public void TryRead_RejectsUnknownPolicyProperties()
    {
        const string json = """{"scheme":"NumericComponents","componentCount":4,"calendarFormat":null,"tagPattern":"v{version}","allowPrerelease":false,"sources":[],"pushMode":"Auto"}""";

        Assert.False(ProjectVersionPolicyCatalog.TryRead(json, out _, out _));
    }

    [Fact]
    public void TryRead_RoundTripsVersionSources()
    {
        var original = new ProjectVersionPolicy(
            "NumericComponents", 4, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("Directory.Build.props", "Xml", "/Project/PropertyGroup/Version")]);

        var json = ProjectVersionPolicyCatalog.Serialize(original);

        Assert.True(ProjectVersionPolicyCatalog.TryRead(json, out var parsed, out var error), error);
        Assert.Equal(original, parsed);
        Assert.Equal(original.Sources, parsed.Sources);
    }

    [Theory]
    [InlineData("v{version}", true)]
    [InlineData("release-{version}", true)]
    [InlineData("refs/{version}", false)]
    [InlineData("v{version}-{version}", false)]
    [InlineData("../v{version}", false)]
    public void Validate_RequiresSafeTagPattern(string pattern, bool expected)
    {
        var policy = new ProjectVersionPolicy("SemVer", 3, null, pattern, false, []);

        Assert.Equal(expected, ProjectVersionPolicyCatalog.Validate(policy, out _));
    }
}
