namespace ProjectBeacon.Domain.Enums;

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
