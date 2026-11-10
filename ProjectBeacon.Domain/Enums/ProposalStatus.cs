namespace ProjectBeacon.Domain.Enums;

/// <summary>Lifecycle of a human-reviewable content proposal.</summary>
public enum ProposalStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
    Conflicted = 3,
    Superseded = 4,
    Withdrawn = 5,
    Expired = 6
}
