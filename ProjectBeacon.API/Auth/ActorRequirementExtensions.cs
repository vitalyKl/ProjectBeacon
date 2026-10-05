namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;
using Application.Authorization;
using Domain.Enums;

public static class ActorRequirementExtensions
{
    private static bool HasCapability(ApiTokenCapability caps, ApiTokenCapability required)
        => caps.HasFlag(required) || caps.HasFlag(ApiTokenCapability.Admin);

    public static IEndpointConventionBuilder RequireHuman(this IEndpointConventionBuilder builder)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var actor = ctx.HttpContext.GetActor();
            if (!actor.IsHuman || actor.UserId is null)
                return ProblemResults.Forbidden("Human authentication required.");
            return await next(ctx);
        });

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

    public static IEndpointConventionBuilder RequireApiToken(this IEndpointConventionBuilder builder, ApiTokenCapability capability)
        => builder.AddEndpointFilter(async (ctx, next) =>
        {
            var actor = ctx.HttpContext.GetActor();
            if (!actor.IsApiToken || !HasCapability(actor.Capabilities, capability))
                return ProblemResults.Forbidden();
            return await next(ctx);
        });

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
