namespace ProjectBeacon.Application.Billing;

/// <summary>
/// A provider-neutral reference to a payment method. It contains only an external provider
/// reference and display metadata; never PAN, CVV/CVC, track data, bank credentials or raw tokens.
/// </summary>
public sealed record BillingPaymentMethodReference(
    string ProviderKey,
    string ExternalCustomerReference,
    string ExternalPaymentMethodReference,
    string? Brand,
    string? LastFour,
    int? ExpirationMonth,
    int? ExpirationYear,
    bool IsDefault);

public sealed record BillingCheckoutRequest(
    Guid OrgId,
    string PlanKey,
    string SuccessUrl,
    string CancelUrl,
    string IdempotencyKey);

public sealed record BillingCheckoutSession(
    string ProviderKey,
    string ExternalSessionReference,
    Uri RedirectUri,
    DateTimeOffset? ExpiresAtUtc);

public sealed record BillingPortalRequest(
    Guid OrgId,
    string ExternalCustomerReference,
    string ReturnUrl);

public sealed record BillingPortalSession(
    string ProviderKey,
    Uri RedirectUri,
    DateTimeOffset? ExpiresAtUtc);

public sealed record BillingSubscriptionChangeRequest(
    string ExternalSubscriptionReference,
    string PlanKey,
    bool Prorate);

public sealed record BillingSubscriptionCancelRequest(
    string ExternalSubscriptionReference,
    bool AtPeriodEnd);

public sealed record BillingSubscriptionSnapshot(
    string ProviderKey,
    string ExternalSubscriptionReference,
    string StatusKey,
    DateTimeOffset? CurrentPeriodStartsAtUtc,
    DateTimeOffset? CurrentPeriodEndsAtUtc,
    bool CancelAtPeriodEnd,
    BillingPaymentMethodReference? DefaultPaymentMethod);

/// <summary>
/// Raw webhook bytes are passed to the provider adapter only for signature verification and
/// normalization. They must not be persisted or copied to application logs.
/// </summary>
public sealed record BillingWebhookRequest(
    ReadOnlyMemory<byte> RawBody,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A verified, normalized provider event. ExternalEventId is the idempotency key; the raw
/// provider payload is deliberately not part of this contract.
/// </summary>
public sealed record BillingProviderEvent(
    string ProviderKey,
    string ExternalEventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string? ExternalCustomerReference,
    string? ExternalSubscriptionReference,
    long? AmountMinor,
    string? Currency,
    BillingPaymentMethodReference? PaymentMethod);