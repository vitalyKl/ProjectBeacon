namespace ProjectBeacon.Domain.Enums;

/// <summary>Mutation proposed against an existing, accepted content target.</summary>
public enum ProposalOperation
{
    Create = 0,
    Update = 1,
    Delete = 2
}
