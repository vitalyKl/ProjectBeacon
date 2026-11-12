namespace ProjectBeacon.Application.Admin;

/// <summary>
/// Named permission sets. SystemAdministrator retains the legacy IsAdmin account marker;
/// every other persisted role receives only its explicitly mapped capabilities.
/// </summary>
public static class OperatorPermissionSets
{
    public const OperatorPermission CurrentSystemAdministrator =
        (OperatorPermission)((1UL << 16) - 1);

    public static bool Includes(OperatorPermission granted, OperatorPermission required) =>
        required != OperatorPermission.None && (granted & required) == required;
}