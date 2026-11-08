namespace ProjectBeacon.Application.Billing;

/// <summary>
/// Persistence port for provider-owned payment-method references and masked display data.
/// Implementations must never accept or store raw card or bank credentials.
/// </summary>
public interface IPaymentMethodReferenceStore
{
    Task<IReadOnlyList<BillingPaymentMethodReference>> ListAsync(
        Guid orgId,
        CancellationToken ct = default);

    Task SaveAsync(
        Guid orgId,
        BillingPaymentMethodReference reference,
        CancellationToken ct = default);

    Task RemoveAsync(
        Guid orgId,
        string providerKey,
        string externalPaymentMethodReference,
        CancellationToken ct = default);
}