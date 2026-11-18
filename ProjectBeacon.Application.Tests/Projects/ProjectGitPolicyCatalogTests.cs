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
    public void TryRead_RecognizesPersistedIntegrationPolicy()
    {
        const string json = "{\"autonomyLevel\":1,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}";

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 4, out var policy));
        Assert.Equal(1, policy.AutonomyLevel);
        Assert.True(policy.IntegrationBranchAvailable);
        Assert.Null(policy.IntegrationBranchUnavailableReason);
    }

    [Fact]
    public async Task SetPolicy_RejectsUnknownAutonomyLevelBeforePersistence()
    {
        var handler = new SetProjectGitPolicyHandler(null!, null!, null!);
        var actor = new ProjectBeacon.Application.Authorization.ActorContext(
            ProjectBeacon.Application.Authorization.ActorType.Human,
            Guid.NewGuid(),
            false,
            null,
            null,
            null,
            null,
            ProjectBeacon.Domain.Enums.ApiTokenCapability.None);
        var command = new SetProjectGitPolicyCommand(
            Guid.NewGuid(), 3, "Escalate", [], 200, 20_000, "Off", 1, actor);

        var result = await handler.HandleAsync(command);

        Assert.False(result.Success);
        Assert.Contains("Autonomy level must be 0, 1, or 2", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"autonomyLevel\":3,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Unknown\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
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
    [InlineData(0, "Escalate")]
    [InlineData(1, "Escalate")]
    [InlineData(2, "Escalate")]
    [InlineData(0, "LandViaIntegration")]
    [InlineData(1, "LandViaIntegration")]
    [InlineData(2, "LandViaIntegration")]
    public void Serialize_PreservesSupportedDirtyCheckoutAndAutonomyPolicy(int autonomyLevel, string behavior)
    {
        var json = ProjectGitPolicyCatalog.Serialize(
            autonomyLevel, [], 200, 20_000, "Off", behavior, "main");

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 1, out var policy));
        Assert.Equal(autonomyLevel, policy.AutonomyLevel);
        Assert.Equal(behavior, policy.DirtyCheckoutBehavior);
        Assert.Equal("main", policy.ReviewBaseBranch);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/2026.11", true)]
    [InlineData("beacon/integration", true)]
    [InlineData("../outside", false)]
    [InlineData("main..other", false)]
    [InlineData("main\\\\other", false)]
    [InlineData("main name", false)]
    public void IsSafeBranchName_RejectsUnsafeReferences(string branchName, bool expected)
    {
        Assert.Equal(expected, ProjectGitPolicyCatalog.IsSafeBranchName(branchName));
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
