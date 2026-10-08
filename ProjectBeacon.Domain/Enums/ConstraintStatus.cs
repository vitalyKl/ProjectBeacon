namespace ProjectBeacon.Domain.Enums;

/// <summary>Proposed constraints do not affect compile until <see cref="Active"/>.</summary>
public enum ConstraintStatus
{
    Proposed = 0,
    Active = 1,
    Rejected = 2
}
