namespace ProjectBeacon.Domain.Entities.Audit;

/// <summary>Outcome of the audited operation or security decision.</summary>
public enum AuditOutcome
{
    Success = 0,
    Denied = 1,
    Failed = 2
}
