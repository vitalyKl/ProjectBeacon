namespace ProjectBeacon.Web.Authentication;

/// <summary>
/// Authentication defaults for the isolated platform-operator Control Plane.
/// The cookie is host-only: no Domain attribute is set, so it is never shared with app/API origins.
/// </summary>
public static class ControlAuthenticationDefaults
{
    public const string Scheme = "ControlCookie";
    public const string CookieName = "BeaconControlAuth";
}
