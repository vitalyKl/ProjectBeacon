namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Stable Control Plane role keys persisted on the user account. Effective permissions
/// are derived by OperatorRolePermissionSets and checked by DatabaseAdminGate.
/// </summary>
public enum OperatorRole
{
    SystemAdministrator = 0,
    Support = 1,
    Billing = 2,
    Auditor = 3
}