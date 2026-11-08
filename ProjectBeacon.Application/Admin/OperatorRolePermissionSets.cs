namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Central mapping from future operator roles to permission sets. Handlers depend on
/// permissions only; role assignment and persistence can be introduced without changing them.
/// </summary>
public static class OperatorRolePermissionSets
{
    private const OperatorPermission SupportPermissions =
        OperatorPermission.OrganizationsRead
        | OperatorPermission.UsersRead
        | OperatorPermission.UsersManage
        | OperatorPermission.EntitlementsRead
        | OperatorPermission.OperationsRead
        | OperatorPermission.ContainerLogsRead
        | OperatorPermission.AuditRead;

    private const OperatorPermission BillingPermissions =
        OperatorPermission.OrganizationsRead
        | OperatorPermission.EntitlementsRead
        | OperatorPermission.EntitlementsManage
        | OperatorPermission.BillingRead
        | OperatorPermission.BillingManage
        | OperatorPermission.AuditRead;

    private const OperatorPermission AuditorPermissions =
        OperatorPermission.OrganizationsRead
        | OperatorPermission.UsersRead
        | OperatorPermission.EntitlementsRead
        | OperatorPermission.BillingRead
        | OperatorPermission.OperationsRead
        | OperatorPermission.AuditRead
        | OperatorPermission.AuditExport;

    public static OperatorPermission For(OperatorRole role) => role switch
    {
        OperatorRole.SystemAdministrator => OperatorPermissionSets.CurrentSystemAdministrator,
        OperatorRole.Support => SupportPermissions,
        OperatorRole.Billing => BillingPermissions,
        OperatorRole.Auditor => AuditorPermissions,
        _ => OperatorPermission.None
    };
}