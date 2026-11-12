using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Common;

namespace ProjectBeacon.Application.Entitlements;

/// <summary>Stable product-owned capability keys required by the runtime access boundary.</summary>
public static class EntitlementFeatureKeys
{
    /// <summary>Baseline entitlement required for any tenant-scoped ProjectBeacon API operation.</summary>
    public const string Core = "core";
}

/// <summary>Product-owned capacity keys understood by runtime enforcement.</summary>
public static class EntitlementLimitKeys
{
    /// <summary>Maximum number of projects an organization may own.</summary>
    public const string Projects = "projects";
    /// <summary>Maximum number of organization memberships, including reserved valid invites.</summary>
    public const string Members = "members";
}


/// <summary>The origin of an organization's effective access grant.</summary>
public enum EntitlementSource
{
    AlphaProgram = 0,
    Subscription = 1,
    Complimentary = 2,
    OperatorOverride = 3
}

/// <summary>Lifecycle of an access grant, independent of the organization's lifecycle.</summary>
public enum EntitlementStatus
{
    Scheduled = 0,
    Active = 1,
    Suspended = 2,
    Expired = 3,
    Revoked = 4
}

/// <summary>Sources an operator may grant directly. Subscription access is created only by the production conversion/billing flow.</summary>
public enum OperatorEntitlementSource
{
    AlphaProgram = 0,
    Complimentary = 1,
    OperatorOverride = 2
}

/// <summary>
/// A named, non-negative limit. Keys are stable product capabilities, not provider-specific
/// price identifiers; the licensed units can be decided without changing the contract.
/// </summary>
public sealed record EntitlementLimit(string Key, long Value);

/// <summary>
/// The effective access snapshot for one organization. Alpha and paid subscriptions produce
/// the same shape, so converting an organization does not require changing feature checks.
/// </summary>
public sealed record OrganizationEntitlement(
    Guid Id,
    Guid OrgId,
    EntitlementSource Source,
    EntitlementStatus Status,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    IReadOnlyDictionary<string, long> Limits,
    IReadOnlySet<string> Features,
    string? SourceReference = null,
    Guid? GrantedByUserId = null,
    string? Reason = null)
{
    public bool IsEffectiveAt(DateTimeOffset utcNow) =>
        Status == EntitlementStatus.Active
        && StartsAtUtc <= utcNow
        && (EndsAtUtc is null || utcNow < EndsAtUtc.Value);
}

/// <summary>
/// A feature or capacity check expressed in product terms. Limit keys and feature keys are
/// owned by ProjectBeacon, never copied from a payment provider's API.
/// </summary>
public sealed record EntitlementRequirement(
    string FeatureKey,
    string? LimitKey = null,
    long RequestedUnits = 1,
    long CurrentUsage = 0);

public sealed record EntitlementDecision(
    bool Allowed,
    string? ReasonCode = null,
    long? CurrentUsage = null,
    long? Limit = null);

/// <summary>
/// Operator-issued access. Alpha grants use AlphaProgram; future commercial flows create
/// Subscription grants from the same contract. End times are exclusive.
/// </summary>
public sealed record GrantOrganizationEntitlementRequest(
    Guid OrgId,
    OperatorEntitlementSource Source,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    IReadOnlyDictionary<string, long> Limits,
    IReadOnlySet<string> Features,
    string Reason);

/// <summary>
/// An operator-triggered alpha-to-production conversion. PlanKey is product-owned; the
/// billing reference may identify a paid invoice or an externally confirmed subscription,
/// but must never contain a payment credential.
/// </summary>
public sealed record ConvertAlphaToProductionRequest(
    Guid OrgId,
    string PlanKey,
    string BillingReference,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    string Reason,
    string IdempotencyKey);

/// <summary>Read-only evaluator used by product mutations before consuming a capability or capacity.</summary>
public interface IEntitlementEvaluator
{
    Task<Result<EntitlementDecision>> EvaluateAsync(
        Guid orgId,
        EntitlementRequirement requirement,
        CancellationToken ct = default);
}

/// <summary>
/// Central read/write boundary for organization access. Implementations must make grant,
/// revoke and source-transition writes auditable and transactional. Conversion to a paid
/// plan is one operation so alpha access cannot be dropped before production access exists.
/// </summary>
public interface IEntitlementService : IEntitlementEvaluator
{
    Task<Result<OrganizationEntitlement?>> GetEffectiveAsync(
        Guid orgId,
        CancellationToken ct = default);

    Task<Result<OrganizationEntitlement?>> GetForOperatorAsync(
        AdminActor actor,
        Guid orgId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<OrganizationEntitlement>>> ListForOperatorAsync(
        AdminActor actor,
        Guid orgId,
        CancellationToken ct = default);

    Task<Result<OrganizationEntitlement>> GrantAsync(
        GrantOrganizationEntitlementRequest request,
        AdminActor actor,
        CancellationToken ct = default);

    Task<Result> RevokeAsync(
        Guid entitlementId,
        string reason,
        AdminActor actor,
        CancellationToken ct = default);

    Task<Result<OrganizationEntitlement>> ConvertAlphaToProductionAsync(
        ConvertAlphaToProductionRequest request,
        AdminActor actor,
        CancellationToken ct = default);
}