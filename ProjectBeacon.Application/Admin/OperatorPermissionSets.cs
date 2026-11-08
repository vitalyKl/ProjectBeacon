namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Named permission sets. The first release maps the existing IsAdmin account flag to
/// CurrentSystemAdministrator. Future operator roles map to subsets without changing handlers.
/// </summary>
public static class OperatorPermissionSets
{
    public const OperatorPermission CurrentSystemAdministrator =
        (OperatorPermission)((1UL << 14) - 1);

    public static bool Includes(OperatorPermission granted, OperatorPermission required) =>
        required != OperatorPermission.None && (granted & required) == required;
}