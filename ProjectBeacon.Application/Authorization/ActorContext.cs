namespace ProjectBeacon.Application.Authorization;

using Domain.Enums;

/// <summary>Kind of actor performing an operation.</summary>
public enum ActorType
{
    Human = 0,
    ApiToken = 1,
    Device = 2,
    Worker = 3,
}

/// <summary>
/// Security/authorization identity of the caller. Resolved once at the HTTP boundary
/// (<see cref="ProjectBeacon.API.Auth.ActorContextFactory"/>) and carried into Application
/// handlers; never reconstructed from client input. Distinct from the <c>ActorId</c>
/// execution/audit identity carried by Pipeline/Eval/Work/FinishWork commands.
/// </summary>
public readonly record struct ActorContext(
    ActorType Type,
    Guid? UserId,
    bool IsAdmin,
    Guid? TokenId,
    Guid? DeviceId,
    Guid? ProjectId,
    Guid? OrgId,
    ApiTokenCapability Capabilities)
{
    public static readonly ActorContext Anonymous =
        new(ActorType.Human, null, false, null, null, null, null, ApiTokenCapability.None);

    public bool IsHuman => Type == ActorType.Human;
    public bool IsApiToken => Type == ActorType.ApiToken;
    public bool IsDevice => Type == ActorType.Device;
    public bool IsWorker => Type == ActorType.Worker;
}
