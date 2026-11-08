namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Reserved operator role keys. The alpha runtime assigns only SystemAdministrator
/// through the existing User.IsAdmin flag; the other roles are not yet assignable.
/// </summary>
public enum OperatorRole
{
    SystemAdministrator = 0,
    Support = 1,
    Billing = 2,
    Auditor = 3
}