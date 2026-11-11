namespace ProjectBeacon.Application.Tests.Entitlements;

using ProjectBeacon.Application.Entitlements;

public sealed class EntitlementLimitEvaluatorTests
{
    [Fact]
    public void MissingLimitKey_IsUnmetered()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long>(),
            new EntitlementRequirement("core", "projects", RequestedUnits: 1, CurrentUsage: 250));

        Assert.True(decision.Allowed);
        Assert.Equal(250L, decision.CurrentUsage);
        Assert.Null(decision.Limit);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void NoLimitKey_PerformsFeatureOnlyDecision()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 0 },
            new EntitlementRequirement("core"));

        Assert.True(decision.Allowed);
        Assert.Equal(0L, decision.CurrentUsage);
        Assert.Null(decision.Limit);
    }

    [Fact]
    public void RequestedUnitsThatExactlyReachLimit_AreAllowed()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 3 },
            new EntitlementRequirement("core", "projects", RequestedUnits: 1, CurrentUsage: 2));

        Assert.True(decision.Allowed);
        Assert.Equal(2L, decision.CurrentUsage);
        Assert.Equal(3L, decision.Limit);
    }

    [Theory]
    [InlineData(3, 1, 3)]
    [InlineData(2, 2, 3)]
    [InlineData(1, 1, 0)]
    public void RequestsThatExceedConfiguredLimit_AreDenied(long requested, long current, long limit)
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = limit },
            new EntitlementRequirement("core", "projects", requested, current));

        Assert.False(decision.Allowed);
        Assert.Equal("limit_exceeded", decision.ReasonCode);
        Assert.Equal(current, decision.CurrentUsage);
        Assert.Equal(limit, decision.Limit);
    }

    [Fact]
    public void ExplicitZeroLimit_DeniesFirstUnit()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 0 },
            new EntitlementRequirement("core", "projects"));

        Assert.False(decision.Allowed);
        Assert.Equal("limit_exceeded", decision.ReasonCode);
    }

    [Fact]
    public void CapacityArithmetic_DoesNotOverflow()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = long.MaxValue },
            new EntitlementRequirement("core", "projects", RequestedUnits: 1, CurrentUsage: long.MaxValue));

        Assert.False(decision.Allowed);
        Assert.Equal("limit_exceeded", decision.ReasonCode);
    }

    [Fact]
    public void InvalidNegativeLimit_FailsClosed()
    {
        var decision = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = -1 },
            new EntitlementRequirement("core", "projects"));

        Assert.False(decision.Allowed);
        Assert.Equal("invalid_limit", decision.ReasonCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    public void InvalidUsageInputs_AreRejected(long requested, long current)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EntitlementLimitEvaluator.EvaluateCapacity(
                new Dictionary<string, long> { ["projects"] = 3 },
                new EntitlementRequirement("core", "projects", requested, current)));
    }
}
