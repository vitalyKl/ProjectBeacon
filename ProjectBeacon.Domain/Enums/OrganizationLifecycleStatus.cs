namespace ProjectBeacon.Domain.Enums;

/// <summary>
/// Operational lifecycle of a hosted customer organization. Closed is terminal and does not
/// delete customer data; entitlement state remains a separate concern.
/// </summary>
public enum OrganizationLifecycleStatus
{
    Active = 0,
    Suspended = 1,
    Closed = 2
}
