namespace ProjectBeacon.Application.Billing;

/// <summary>
/// Starts a provider-hosted payment-method setup flow. The browser collects payment details
/// directly with the provider; ProjectBeacon receives only the redirect URL and opaque refs.
/// </summary>
public sealed record BillingPaymentMethodSetupRequest(
    Guid OrgId,
    string ExternalCustomerReference,
    string ReturnUrl,
    string IdempotencyKey);

public sealed record BillingPaymentMethodSetupSession(
    string ProviderKey,
    string ExternalSessionReference,
    Uri RedirectUri,
    DateTimeOffset? ExpiresAtUtc);