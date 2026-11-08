namespace ProjectBeacon.Application.Billing;

using ProjectBeacon.Application.Common;

/// <summary>
/// Provider adapter boundary. Provider SDK types and raw webhook payloads must not escape
/// Infrastructure or be referenced by Domain, Application handlers, or Web components.
/// </summary>
public interface IBillingProvider
{
    string ProviderKey { get; }

    Task<Result<BillingCheckoutSession>> CreateCheckoutSessionAsync(
        BillingCheckoutRequest request,
        CancellationToken ct = default);

    Task<Result<BillingPortalSession>> CreateCustomerPortalSessionAsync(
        BillingPortalRequest request,
        CancellationToken ct = default);

    Task<Result<BillingSubscriptionSnapshot>> RetrieveSubscriptionAsync(
        string externalSubscriptionReference,
        CancellationToken ct = default);

    Task<Result<BillingSubscriptionSnapshot>> ChangeSubscriptionAsync(
        BillingSubscriptionChangeRequest request,
        CancellationToken ct = default);

    Task<Result<BillingSubscriptionSnapshot>> CancelSubscriptionAsync(
        BillingSubscriptionCancelRequest request,
        CancellationToken ct = default);

    Task<Result<BillingProviderEvent>> VerifyAndNormalizeWebhookAsync(
        BillingWebhookRequest request,
        CancellationToken ct = default);
}