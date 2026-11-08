namespace ProjectBeacon.Application.Auth;

/// <summary>
/// Supported registration strategies. Only InviteOnly is enabled for the SaaS alpha.
/// The other modes are reserved contracts, not currently selectable runtime behavior.
/// </summary>
public enum RegistrationMode
{
    InviteOnly = 0,
    ApprovalRequired = 1,
    OpenWithVerifiedEmail = 2
}

/// <summary>
/// Registration rules are explicit and independently testable. Open registration requires
/// verified email; approval-based registration must not create an active account before review.
/// </summary>
public sealed record RegistrationPolicy(
    RegistrationMode Mode,
    bool RequiresEmailVerification,
    bool RequiresOperatorApproval,
    bool AllowsSelfServiceOrganizationCreation)
{
    public static RegistrationPolicy SaaSAlpha { get; } =
        new(RegistrationMode.InviteOnly, false, false, false);

    public void Validate()
    {
        if (Mode == RegistrationMode.OpenWithVerifiedEmail && !RequiresEmailVerification)
            throw new InvalidOperationException("Open registration requires verified email.");

        if (Mode == RegistrationMode.ApprovalRequired && !RequiresOperatorApproval)
            throw new InvalidOperationException("Approval-required registration must require operator approval.");

        if (Mode != RegistrationMode.OpenWithVerifiedEmail && AllowsSelfServiceOrganizationCreation)
            throw new InvalidOperationException("Only verified open registration may create an organization without an operator invitation.");
    }
}

/// <summary>Supplies the effective registration policy to registration handlers.</summary>
public interface IRegistrationPolicyProvider
{
    RegistrationPolicy GetCurrent();
}