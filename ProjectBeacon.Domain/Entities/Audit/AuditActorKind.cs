namespace ProjectBeacon.Domain.Entities.Audit;

public enum AuditActorKind
{
    Human = 0,
    ApiToken = 1,
    Device = 2,
    Worker = 3,
    System = 4
}