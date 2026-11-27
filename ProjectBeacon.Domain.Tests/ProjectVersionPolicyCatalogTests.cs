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

    [Theory]
    [InlineData("//Version[1]")]
    [InlineData("//Version[@Condition='release']")]
    [InlineData("//*[local-name()='Version']")]
    [InlineData("//Version | //PackageVersion")]
    [InlineData("/Project/PropertyGroup/Version/text()")]
    public void Validate_RejectsXmlXPathOutsideTheReleaseCiSupportedSubset(string selector)
    {
        var policy = new ProjectVersionPolicy(
            "NumericComponents", 4, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("manifest.xml", "Xml", selector)]);

        Assert.False(ProjectVersionPolicyCatalog.Validate(policy, out _));
    }

    [Theory]
    [InlineData("yyyy.MM.dd", true)]
    [InlineData("yyyyMMdd", true)]
    [InlineData("yyyy-MM-ddTHH:mm:ss", true)]
    [InlineData("yyyy-MM-ddTHH:mm:ssZ", true)]
    [InlineData("yy.MM.dd", false)]
    [InlineData("yyyy.MM", false)]
    [InlineData("yyyy.MM.dd.fff", false)]
    [InlineData("yyyy.MM.dd 'UTC'", false)]
    public void Validate_RestrictsCalendarFormatsToThePortableReleaseCiTokenSet(string format, bool expected)
    {
        var policy = new ProjectVersionPolicy("Calendar", 0, format, "v{version}", false, []);

        Assert.Equal(expected, ProjectVersionPolicyCatalog.Validate(policy, out _));
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
    [InlineData("Xml", "//Version", "<Project><PropertyGroup><Version>0.7.5.8</Version></PropertyGroup></Project>")]
    [InlineData("Xml", "//PropertyGroup/Version", "<Project><PropertyGroup><Version>0.7.5.8</Version></PropertyGroup></Project>")]
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
    [InlineData(@"^version=(?<version>[0-9.]+)$", true)]
    [InlineData(@"(?=version=)version=(?<version>\d+)", true)]
    [InlineData(@"version=(?<version>\d+)\k<version>", true)]
    [InlineData(@"version=(?<version>\w+)", false)]
    [InlineData(@"version=(?<version>\0)", false)]
    [InlineData(@"version=(?<version>\a)", false)]
    [InlineData(@"version=(?<version>\x30+)", false)]
    [InlineData(@"version=(?<version>\p{L}+)", false)]
    [InlineData(@"version=(?<version>\d+)\1", false)]
    [InlineData(@"version=(?<version>\d+)\k<other>", false)]
    [InlineData(@"version=\k<version>(?<version>\d+)", false)]
    [InlineData(@"(?i)version=(?<version>\d+)", false)]
    [InlineData(@"version=(?<version>[a-z-[aeiou]]+)", false)]
    [InlineData(@"version=(?<version>[0-9]+)(?<other>[0-9]+)", false)]
    public void Validate_UsesTheSamePortableRegexGrammarAsTheReleaseGate(string selector, bool expected)
    {
        var policy = new ProjectVersionPolicy(
            "NumericComponents", 1, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("manifest.txt", "Regex", selector)]);

        Assert.Equal(expected, ProjectVersionPolicyCatalog.Validate(policy, out _));
    }

    [Fact]
    public void ExtractTomlVersion_AcceptsOnlyStringAndIntegerScalars()
    {
        var policy = new ProjectVersionPolicy("NumericComponents", 1, null, "v{version}", false, []);
        var source = new ProjectVersionSourcePolicy("manifest.toml", "Toml", "version");

        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, "version = \"7\"", out var textVersion, out var textError), textError);
        Assert.Equal("7", textVersion);

        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, "version = 7", out var integerVersion, out var integerError), integerError);
        Assert.Equal("7", integerVersion);

        Assert.False(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, "version = 1.25", out _, out _));
        Assert.False(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, "version = true", out _, out _));
        Assert.False(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, "version = [7]", out _, out _));
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
    public void ExtractJsonPointer_SupportsArrayIndexes()
    {
        var policy = new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, []);
        var source = new ProjectVersionSourcePolicy("package.json", "Json", "/versions/0");

        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, """{"versions":["0.7.5.8"]}""", out var version, out var error), error);
        Assert.Equal("0.7.5.8", version);
    }

    [Fact]
    public void ExtractJsonPointer_SupportsRootAndEscapedObjectKeys()
    {
        var policy = new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, []);
        var root = new ProjectVersionSourcePolicy("version.json", "Json", "");
        var escaped = new ProjectVersionSourcePolicy("package.json", "Json", "/a~1b/m~0n");

        Assert.True(ProjectVersionPolicyCatalog.Validate(policy with { Sources = [root, escaped] }, out var policyError), policyError);
        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, root, "\"0.7.5.8\"", out var rootVersion, out var rootError), rootError);
        Assert.Equal("0.7.5.8", rootVersion);
        Assert.True(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, escaped, """{"a/b":{"m~n":"0.7.5.8"}}""", out var escapedVersion, out var escapedError), escapedError);
        Assert.Equal("0.7.5.8", escapedVersion);
    }

    [Theory]
    [InlineData("/version~")]
    [InlineData("/version~2name")]
    [InlineData("/version~~0")]
    public void Validate_RejectsInvalidJsonPointerEscapeSequences(string selector)
    {
        var policy = new ProjectVersionPolicy(
            "NumericComponents", 4, null, "v{version}", false,
            [new ProjectVersionSourcePolicy("package.json", "Json", selector)]);

        Assert.False(ProjectVersionPolicyCatalog.Validate(policy, out _));
    }

    [Theory]
    [InlineData("/versions/00")]
    [InlineData("/versions/٠")]
    [InlineData("/versions/1")]
    public void ExtractJsonPointer_RejectsInvalidOrOutOfRangeArrayIndexes(string selector)
    {
        var policy = new ProjectVersionPolicy("NumericComponents", 4, null, "v{version}", false, []);
        var source = new ProjectVersionSourcePolicy("package.json", "Json", selector);

        Assert.False(ProjectVersionPolicyCatalog.TryExtractVersion(
            policy, source, """{"versions":["0.7.5.8"]}""", out _, out _));
    }

    [Fact]
    public void ProjectOfficialVersion_CanOnlyBeInitializedOnceOutsideReleaseFlow()
    {
        var project = Project.Create("Versioned project", null, Guid.NewGuid());

        project.InitializeOfficialVersion("0.7.5.8");

        Assert.Equal("0.7.5.8", project.OfficialVersion);
        Assert.Throws<InvalidOperationException>(
            () => project.InitializeOfficialVersion("0.7.5.9"));
    }

    [Theory]
    [InlineData("https://beacon.example/v1/version", true)]
    [InlineData("http://beacon.example/v1/version", false)]
    [InlineData("https://beacon.example/version", false)]
    [InlineData("https://user:secret@beacon.example/v1/version", false)]
    [InlineData("https://beacon.example/v1/version?token=x", false)]
    [InlineData("https://beacon.example/v1/version#fragment", false)]
    public void Validate_RequiresSafeDeploymentVersionEndpoint(string endpoint, bool expected)
    {
        var policy = new ProjectVersionPolicy(
            "SemVer", 3, null, "v{version}", false, [],
            DeploymentVersionEndpoint: endpoint);

        Assert.Equal(expected, ProjectVersionPolicyCatalog.Validate(policy, out _));
    }

    [Fact]
    public void TryRead_RoundTripsDeploymentVersionEndpoint()
    {
        var original = new ProjectVersionPolicy(
            "SemVer", 3, null, "v{version}", false, [],
            DeploymentVersionEndpoint: "https://beacon.example/v1/version");

        var json = ProjectVersionPolicyCatalog.Serialize(original);

        Assert.True(ProjectVersionPolicyCatalog.TryRead(json, out var parsed, out var error), error);
        Assert.Equal(original.DeploymentVersionEndpoint, parsed.DeploymentVersionEndpoint);
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
        Assert.Equal(original.Scheme, parsed.Scheme);
        Assert.Equal(original.ComponentCount, parsed.ComponentCount);
        Assert.Equal(original.TagPattern, parsed.TagPattern);
        Assert.Equal(original.Sources.Count, parsed.Sources.Count);
        Assert.Equal(original.Sources[0], parsed.Sources[0]);
    }

    [Theory]
    [InlineData("v{version}", true)]
    [InlineData("release-{version}", true)]
    [InlineData("/v{version}", false)]
    [InlineData("v{version}-{version}", false)]
    [InlineData("../v{version}", false)]
    [InlineData("release/{version}/", false)]
    [InlineData(".release-{version}", false)]
    [InlineData("v{version}.lock", false)]
    [InlineData("release//v{version}", false)]
    public void Validate_RequiresSafeTagPattern(string pattern, bool expected)
    {
        var policy = new ProjectVersionPolicy("SemVer", 3, null, pattern, false, []);

        Assert.Equal(expected, ProjectVersionPolicyCatalog.Validate(policy, out _));
    }
}
