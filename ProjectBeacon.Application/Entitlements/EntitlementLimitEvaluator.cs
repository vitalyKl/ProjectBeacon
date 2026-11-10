namespace ProjectBeacon.Application.Entitlements;

/// <summary>
/// Applies an optional product-owned capacity cap to an entitlement snapshot.
/// Missing limit keys are intentionally unmetered; an explicitly configured zero is a zero cap.
/// </summary>
public static class EntitlementLimitEvaluator
{
    public static EntitlementDecision EvaluateCapacity(
        IReadOnlyDictionary<string, long> limits,
        EntitlementRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(requirement);

        if (requirement.RequestedUnits < 1 || requirement.CurrentUsage < 0)
            throw new ArgumentOutOfRangeException(nameof(requirement), "Capacity checks require positive requested units and non-negative current usage.");

        if (requirement.LimitKey is null)
            return new EntitlementDecision(true, CurrentUsage: requirement.CurrentUsage);

        var key = requirement.LimitKey.Trim().ToLowerInvariant();
        if (key.Length == 0)
            throw new ArgumentException("Limit key cannot be empty.", nameof(requirement));

        if (!limits.TryGetValue(key, out var limit))
            return new EntitlementDecision(true, CurrentUsage: requirement.CurrentUsage);

        if (limit < 0)
            return new EntitlementDecision(false, "invalid_limit", requirement.CurrentUsage, limit);

        var allowed = requirement.CurrentUsage <= limit
            && requirement.RequestedUnits <= limit - requirement.CurrentUsage;
        return new EntitlementDecision(
            allowed,
            allowed ? null : "limit_exceeded",
            requirement.CurrentUsage,
            limit);
    }
}
