namespace ProjectBeacon.Domain.Enums;

/// <summary>A decision is proposed, then accepted. Accepted decisions are superseded or deprecated.</summary>
public enum DecisionStatus
{
    Proposed = 0,
    Accepted = 1,
    Superseded = 2,
    Deprecated = 3
}
