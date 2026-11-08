namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Operator-console permissions. These are deliberately separate from project membership
/// roles and ApiTokenCapability; machine tokens never receive operator permissions.
/// </summary>
[Flags]
public enum OperatorPermission : ulong
{
    None = 0,
    OrganizationsRead = 1UL << 0,
    OrganizationsManage = 1UL << 1,
    UsersRead = 1UL << 2,
    UsersManage = 1UL << 3,
    EntitlementsRead = 1UL << 4,
    EntitlementsManage = 1UL << 5,
    BillingRead = 1UL << 6,
    BillingManage = 1UL << 7,
    OperationsRead = 1UL << 8,
    ContainerLogsRead = 1UL << 9,
    AuditRead = 1UL << 10,
    AuditExport = 1UL << 11,
    SystemMaintenance = 1UL << 12,
}