namespace ProjectBeacon.Domain.Enums;

/// <summary>
/// Capabilities on a project API token (<c>bcn_</c>). Flags combine. Wire values are these names.
/// <see cref="Admin"/> is required for pipeline force-close. It does not grant project administer.
/// </summary>
[Flags]
public enum ApiTokenCapability : long
{
    None = 0,
    TaskRead = 1L << 0,
    TaskWrite = 1L << 1,
    SessionDrive = 1L << 2,
    ContextRead = 1L << 3,
    Admin = 1L << 4
}
