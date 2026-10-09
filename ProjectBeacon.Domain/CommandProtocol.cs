namespace ProjectBeacon.Domain;

/// <summary>
/// Version of the workstation command wire protocol. Bump only when the envelope or payload
/// shape changes in a way an older client could not interpret.
/// </summary>
public static class CommandProtocol
{
    public const int CurrentVersion = 1;
}
