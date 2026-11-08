namespace ProjectBeacon.Domain.Entities.Audit;

/// <summary>The principal category responsible for an audited action.</summary>
public enum AuditActorKind
{
    Human = 0,
    ApiToken = 1,
    Device = 2,
    Worker = 3,
    System = 4
}
