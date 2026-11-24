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
        Assert.Equal("Escalate", policy.DirtyCheckoutBehavior);
        Assert.Empty(policy.ProtectedPaths);
        Assert.Equal(200, policy.MaxChangedFiles);
        Assert.Equal(20_000, policy.MaxChangedLines);
        Assert.Equal("Off", policy.AutoRevertMode);
        Assert.True(policy.IntegrationBranchAvailable);
        Assert.False(policy.AutoSyncEnabled);
    }

    [Fact]
    public void Serialize_RoundTripsCanonicalProjectPolicy()
    {
        var json = ProjectGitPolicyCatalog.Serialize(
            1,
            ["src/generated/**", "docs/private/*.md"],
            40,
            2_500,
            "OnPostCheckFail",
            "LandViaIntegration",
            autoSyncEnabled: true);

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 7, out var policy));
        Assert.Equal(1, policy.AutonomyLevel);
        Assert.Equal("LandViaIntegration", policy.DirtyCheckoutBehavior);
        Assert.Equal(7, policy.Revision);
        Assert.Equal(40, policy.MaxChangedFiles);
        Assert.Equal(2_500, policy.MaxChangedLines);
        Assert.Equal("OnPostCheckFail", policy.AutoRevertMode);
        Assert.True(policy.AutoSyncEnabled);
        Assert.Equal(new[] { "docs/private/*.md", "src/generated/**" }, policy.ProtectedPaths);
    }

    [Fact]
    public void TryRead_LegacyPolicyWithoutAutoSyncFlagDefaultsDisabled()
    {
        const string json = "{\\"autonomyLevel\\":0,\\"dirtyCheckoutBehavior\\":\\"Escalate\\",\\"protectedPaths\\":[],\\"maxChangedFiles\\":200,\\"maxChangedLines\\":20000,\\"autoRevertMode\\":\\"Off\\"}";

        Assert.True(ProjectGitPolicyCatalog.TryRead(json, 4, out var policy));
        Assert.False(policy.AutoSyncEnabled);
    }

    [Fact]
    public void TryRead_RejectsNonBooleanAutoSyncFlag()
    {
        const string json = "{\\"autonomyLevel\\":0,\\"dirtyCheckoutBehavior\\":\\"Escalate\\",\\"protectedPaths\\":[],\\"maxChangedFiles\\":200,\\"maxChangedLines\\":20000,\\"autoRevertMode\\":\\"Off\\",\\"autoSyncEnabled\\":\\"yes\\"}";

        Assert.False(ProjectGitPolicyCatalog.TryRead(json, 1, out _));
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
    public async Task SetPolicy_RejectsUnsupportedAutonomyLevelBeforePersistence()
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

    [Fact]
    public void SerializeAttemptSnapshot_PreservesReviewedBaseBranchIndependentlyOfLandingTarget()
    {
        var policy = new ProjectGitPolicyDto(
            1, "Escalate", [], 100, 5_000, "Off", 3, true, null);
        var snapshot = ProjectGitPolicyCatalog.SerializeAttemptSnapshot(policy, "main");

        Assert.True(ProjectGitPolicyCatalog.TryRead(snapshot, 3, out var parsed));
        Assert.Equal(1, parsed.AutonomyLevel);
        Assert.True(ProjectGitPolicyCatalog.TryReadReviewBaseBranch(snapshot, "beacon/integration", out var reviewBase));
        Assert.Equal("main", reviewBase);
    }

    [Theory]
    [InlineData("{\"autonomyLevel\":3,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
    [InlineData("{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Unsafe\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\"}")]
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
