namespace ProjectBeacon.Domain.Enums;

/// <summary>Rule class stored on a project constraint. Wire values are these names.</summary>
public enum ConstraintKind
{
    Must = 0,
    MustNot = 1,
    Security = 2,
    Compliance = 3
}
