namespace ProjectBeacon.Application.Billing;

/// <summary>Resolves a configured provider by its stable application-owned key.</summary>
public interface IBillingProviderResolver
{
    IReadOnlyCollection<string> ConfiguredProviderKeys { get; }

    bool TryResolve(string providerKey, out IBillingProvider? provider);
}