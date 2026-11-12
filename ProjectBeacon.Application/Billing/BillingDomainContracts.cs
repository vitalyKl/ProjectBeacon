namespace ProjectBeacon.Application.Billing;

/// <summary>Application-owned billing cadence, independent of a provider's price model.</summary>
public enum BillingCadence
{
    OneTime = 0,
    Monthly = 1,
    Annual = 2
}

/// <summary>Canonical ProjectBeacon subscription state; provider-specific statuses are mapped into this model.</summary>
public enum SubscriptionStatus
{
    Trialing = 0,
    Active = 1,
    PastDue = 2,
    Suspended = 3,
    Cancelled = 4,
    Expired = 5
}

/// <summary>How the commercial relationship is settled.</summary>
public enum SubscriptionSource
{
    ManualInvoice = 0,
    PaymentProvider = 1,
    Complimentary = 2
}

/// <summary>Canonical invoice lifecycle. Overdue is derived from Open plus DueAtUtc, not stored as a competing state.</summary>
public enum BillingInvoiceStatus
{
    Draft = 0,
    Open = 1,
    Paid = 2,
    Void = 3,
    Uncollectible = 4
}

/// <summary>Append-only billing ledger event types.</summary>
public enum BillingLedgerEventType
{
    InvoiceIssued = 0,
    PaymentSucceeded = 1,
    PaymentFailed = 2,
    PaymentRefunded = 3,
    SubscriptionActivated = 4,
    SubscriptionChanged = 5,
    SubscriptionCancelled = 6,
    SubscriptionSuspended = 7,
    ManualMarkedPaid = 8,
    GraceExtended = 9,
    ComplimentaryAccessGranted = 10,
    InvoiceVoided = 11,
    InvoiceMarkedUncollectible = 12
}

/// <summary>
/// Product-owned plan definition. Price and currency remain unset until a commercial model
/// is approved. Limits and features use the same stable keys as OrganizationEntitlement.
/// </summary>
public sealed record PlanDefinition(
    string Key,
    string Name,
    BillingCadence Cadence,
    long? PriceMinor,
    string? Currency,
    IReadOnlyDictionary<string, long> Limits,
    IReadOnlySet<string> Features,
    bool IsActive);

/// <summary>
/// Provider-neutral subscription snapshot. Only opaque external identifiers are retained;
/// the provider adapter maps its native status to SubscriptionStatus.
/// </summary>
public sealed record OrganizationSubscription(
    Guid Id,
    Guid OrgId,
    string PlanKey,
    SubscriptionStatus Status,
    SubscriptionSource Source,
    DateTimeOffset? TrialEndsAtUtc,
    DateTimeOffset CurrentPeriodStartsAtUtc,
    DateTimeOffset? CurrentPeriodEndsAtUtc,
    bool CancelAtPeriodEnd,
    DateTimeOffset? GraceUntilUtc,
    string? ProviderKey,
    string? ExternalCustomerReference,
    string? ExternalSubscriptionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Provider-neutral invoice snapshot. Amounts are integer minor units and Currency is ISO 4217.
/// PaymentReference is a receipt/reference string, never card or bank credentials.
/// </summary>
public sealed record BillingInvoice(
    Guid Id,
    Guid OrgId,
    string Number,
    string Description,
    long AmountMinor,
    string Currency,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DueAtUtc,
    BillingInvoiceStatus Status,
    DateTimeOffset? PaidAtUtc,
    string? PaymentReference,
    string? ProviderKey,
    string? ExternalInvoiceReference);

/// <summary>
/// Immutable billing ledger entry. ProviderEventId is unique within ProviderKey and makes
/// webhook ingestion idempotent. Raw provider payloads and payment credentials are excluded.
/// </summary>
public sealed record BillingLedgerEntry(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    Guid OrgId,
    BillingLedgerEventType EventType,
    SubscriptionSource Source,
    long? AmountMinor,
    string? Currency,
    string? ProviderKey,
    string? ProviderEventId,
    string? Reference,
    Guid? ActorUserId,
    string? Note,
    Guid? SubscriptionId = null,
    string? IdempotencyKey = null,
    Guid? InvoiceId = null);
