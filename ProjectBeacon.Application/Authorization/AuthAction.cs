namespace ProjectBeacon.Application.Authorization;

/// <summary>Action checked against a <see cref="ResourceType"/>.</summary>
public enum AuthAction
{
    Read,
    Create,
    Update,
    Delete,
    Execute,
    Administer,
}
