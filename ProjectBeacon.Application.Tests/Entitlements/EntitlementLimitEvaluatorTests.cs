namespace ProjectBeacon.Application.Tests.Entitlements;

using ProjectBeacon.Application.Entitlements;

public sealed class EntitlementLimitEvaluatorTests
{
    [Fact]
    public void EvaluateCapacity_AllowsUsageWithinLimit()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 10 },
            new EntitlementRequirement("projects.create", "projects", RequestedUnits: 2, CurrentUsage: 7));

        Assert.True(result.Allowed);
        Assert.Null(result.ReasonCode);
        Assert.Equal(7, result.CurrentUsage);
        Assert.Equal(10, result.Limit);
    }

    [Fact]
    public void EvaluateCapacity_RejectsUsageThatWouldExceedLimit()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 10 },
            new EntitlementRequirement("projects.create", "projects", RequestedUnits: 2, CurrentUsage: 9));

        Assert.False(result.Allowed);
        Assert.Equal("limit_exceeded", result.ReasonCode);
        Assert.Equal(9, result.CurrentUsage);
        Assert.Equal(10, result.Limit);
    }

    [Fact]
    public void EvaluateCapacity_AllowsExactLimitBoundary()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 10 },
            new EntitlementRequirement("projects.create", "projects", RequestedUnits: 3, CurrentUsage: 7));

        Assert.True(result.Allowed);
        Assert.Equal(10, result.Limit);
    }

    [Fact]
    public void EvaluateCapacity_TreatsMissingLimitAsUnmetered()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long>(),
            new EntitlementRequirement("projects.create", "projects", RequestedUnits: 3, CurrentUsage: 100));

        Assert.True(result.Allowed);
        Assert.Null(result.Limit);
        Assert.Equal(100, result.CurrentUsage);
    }

    [Fact]
    public void EvaluateCapacity_TreatsNullLimitKeyAsUnmetered()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 0 },
            new EntitlementRequirement("projects.create", CurrentUsage: 5));

        Assert.True(result.Allowed);
        Assert.Null(result.Limit);
        Assert.Equal(5, result.CurrentUsage);
    }

    [Fact]
    public void EvaluateCapacity_RejectsExplicitZeroLimitWhenCapacityIsRequested()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = 0 },
            new EntitlementRequirement("projects.create", "projects"));

        Assert.False(result.Allowed);
        Assert.Equal("limit_exceeded", result.ReasonCode);
        Assert.Equal(0, result.Limit);
    }

    [Fact]
    public void EvaluateCapacity_RejectsNegativeConfiguredLimit()
    {
        var result = EntitlementLimitEvaluator.EvaluateCapacity(
            new Dictionary<string, long> { ["projects"] = -1 },
            new EntitlementRequirement("projects.create", "projects"));

        Assert.False(result.Allowed);
        Assert.Equal("invalid_limit", result.ReasonCode);
        Assert.Equal(-1, result.Limit);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void EvaluateCapacity_RejectsInvalidUsageOrRequestedUnits(long requestedUnits, long currentUsage)
    {
        var requirement = new EntitlementRequirement(
            "projects.create", "projects", requestedUnits, currentUsage);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EntitlementLimitEvaluator.EvaluateCapacity(new Dictionary<string, long>(), requirement));
    }
}
