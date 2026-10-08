namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;
using Application.Authorization;
using Domain.Enums;

/// <summary>
/// Endpoint filters that gate a route on <c>HttpContext.GetActor()</c>. An API token with <c>ApiTokenCapability.Admin</c> satisfies any required capability.
/// </summary>
public static class ActorRequirementExtensions
{
    private static bool HasCapability(ApiTokenCapability caps, ApiTokenCapability required)
        => caps.HasFlag(required) || caps.HasFlag(ApiTokenCapability.Admin);

    /// <summary>
    /// Requires a human actor with <c>UserId</c>. Otherwise returns 403.
    /// </summary>
    public static IEndpointConventionBuilder RequireHuman(this IEndpointConventionBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var actor = ctx.HttpContext.GetActor();
            if (!actor.IsHuman || actor.UserId is null)
                return ProblemResults.Forbidden("Human authentication required.");
            return await next(ctx);
        });

    /// <summary>
    /// Marks the endpoint with <see cref="AllowDeviceActorAttribute"/> and requires a device actor with <c>DeviceId</c>. Otherwise returns 401.
    /// </summary>
    public static IEndpointConventionBuilder RequireDevice(this IEndpointConventionBuilder builder)
        => builder
            .WithMetadata(new AllowDeviceActorAttribute())
            .AddEndpointFilter(async (ctx, next) =>
            {
                var actor = ctx.HttpContext.GetActor();
                if (!actor.IsDevice || actor.DeviceId is null)
                    return ProblemResults.Unauthorized();
                return await next(ctx);
            });

    /// <summary>
    /// Requires an API-token actor whose capabilities include <paramref name="capability"/> or <c>ApiTokenCapability.Admin</c>. Otherwise returns 403.
    /// </summary>
    /// <param name="capability">Capability the token must have. <c>Admin</c> satisfies any value.</param>
    public static IEndpointConventionBuilder RequireApiToken(this IEndpointConventionBuilder builder, ApiTokenCapability capability)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var actor = ctx.HttpContext.GetActor();
            if (!actor.IsApiToken || !HasCapability(actor.Capabilities, capability))
                return ProblemResults.Forbidden();
            return await next(ctx);
        });

    /// <summary>
    /// Allows a human actor with <c>UserId</c>, or an API-token actor whose capabilities include <paramref name="capability"/> or <c>ApiTokenCapability.Admin</c>. Otherwise returns 403.
    /// </summary>
    /// <param name="capability">Capability an API token must have. Ignored for a human actor. <c>Admin</c> satisfies any value.</param>
    public static IEndpointConventionBuilder RequireHumanOrApiToken(this IEndpointConventionBuilder builder, ApiTokenCapability capability)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var actor = ctx.HttpContext.GetActor();
            if (actor.IsHuman && actor.UserId is not null)
                return await next(ctx);
            if (actor.IsApiToken && HasCapability(actor.Capabilities, capability))
                return await next(ctx);
            return ProblemResults.Forbidden();
        });
}
