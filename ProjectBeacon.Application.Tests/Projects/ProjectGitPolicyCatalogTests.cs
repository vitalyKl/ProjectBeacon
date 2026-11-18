namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Projects;

public sealed class ProjectGitPolicyCatalogTests
{
    [Fact]
    public void DefaultPolicy_IsManualAndFailSafe()
    {
        var parsed = ProjectGitPolicyCatalog.TryRead(
            ProjectGitPolicyCatalog.DefaultJson, 1, out var policy);

        Assert.True(parsed);
        Assert.Equal(0, policy.AutonomyLevel);
        Assert.Equal("LandViaIntegration", policy.DirtyCheckoutBehavior);
        Assert.Empty(policy.ProtectedPaths);
        Assert.Equal(200, policy.MaxChangedFiles);
        Assert.Equal(20_000, policy.MaxChangedLines);
        Assert.Equal("Off", policy.AutoRevertMode);
        Assert.True(policy.IntegrationBranchAvailable);
    }

    [Fact]
    public void Serialize_RoundTripsCanonicalProjectPolicy()
    {
        var json = ProjectGitPolicyCatalog.Serialize(
            2,
            ["src/generated/**", "docs/private/*.md"],
            40,
            2_500,
            "OnPostCheckFail",
            "LandViaIntegration");

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 7, out var policy));
        Assert.Equal(2, policy.AutonomyLevel);
        Assert.Equal("Escalate", policy.DirtyCheckoutBehavior);
        Assert.Equal(7, policy.Revision);
        Assert.Equal(40, policy.MaxChangedFiles);
        Assert.Equal(2_500, policy.MaxChangedLines);
        Assert.Equal("OnPostCheckFail", policy.AutoRevertMode);
        Assert.Equal(new[] { "docs/private/*.md", "src/generated/**" }, policy.ProtectedPaths);
    }

    [Fact]
    public void TryRead_RecognizesPersistedIntegrationPolicySoItCanBeRepaired()
    {
        const string json = "{\"autonomyLevel\":1,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}";

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 4, out var policy));
        Assert.Equal(1, policy.AutonomyLevel);
        Assert.True(policy.IntegrationBranchAvailable);
        Assert.Null(policy.IntegrationBranchUnavailableReason);
    }

    [Fact]
    public void IntegrationMode_IsDerivedFromThePinnedPolicy()
    {
        var forced = ProjectGitPolicyCatalog.Serialize(1, [], 200, 20_000, "Off");
        var fallback = ProjectGitPolicyCatalog.Serialize(2, [], 200, 20_000, "Off", "LandViaIntegration");
        var disabled = ProjectGitPolicyCatalog.Serialize(2, [], 200, 20_000, "Off", "Escalate");

        Assert.Equal("Force", ProjectGitPolicyCatalog.IntegrationMode(forced));
        Assert.Equal("Fallback", ProjectGitPolicyCatalog.IntegrationMode(fallback));
        Assert.Equal("None", ProjectGitPolicyCatalog.IntegrationMode(disabled));
    }

    [Theory]
    [InlineData("{\"autonomyLevel\":3,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Unexpected\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":201,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Auto\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[\"../outside\"],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\",\"pushMode\":\"Auto\"}")]
    [InlineData("{\"autonomyLevel\":0,\"autonomyLevel\":2,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    public void TryRead_RejectsUnsupportedOrAmbiguousPolicy(string json)
    {
        Assert.False(ProjectGitPolicyCatalog.TryRead(json, 1, out _));
    }

    [Theory]
    [InlineData("src/generated/**", true)]
    [InlineData("docs/private/*.md", true)]
    [InlineData("src/file?.cs", true)]
    [InlineData("../secrets/**", false)]
    [InlineData("/rooted/**", false)]
    [InlineData("src/../../outside", false)]
    [InlineData("src\\\\outside", false)]
    [InlineData("C:/outside/**", false)]
    [InlineData("src//empty-segment", false)]
    public void IsSafePattern_RequiresProjectRelativeNonTraversalGlob(string pattern, bool expected)
    {
        Assert.Equal(expected, ProjectGitPolicyCatalog.IsSafePattern(pattern));
    }
}
