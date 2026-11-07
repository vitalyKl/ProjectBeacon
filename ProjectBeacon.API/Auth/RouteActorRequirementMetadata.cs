namespace ProjectBeacon.API.Auth;

using Domain.Enums;

/// <summary>Actor boundary declared by a route's endpoint metadata.</summary>
public enum RouteActorRequirement
{
    Human,
    Device,
    ApiToken,
    HumanOrApiToken
}

/// <summary>
/// Describes the actor restriction enforced by <see cref="ActorRequirementExtensions"/>.
/// This metadata is descriptive only; the endpoint filters remain the enforcement boundary.
/// </summary>
public sealed record RouteActorRequirementMetadata(
    RouteActorRequirement Requirement,
    ApiTokenCapability? Capability = null);
