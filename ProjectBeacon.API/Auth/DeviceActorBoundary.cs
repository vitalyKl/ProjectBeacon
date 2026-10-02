namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;

public sealed class AllowDeviceActorAttribute : Attribute
{
}

public static class ActorTypeEndpointExtensions
{
    public static IEndpointConventionBuilder RequireDeviceActor(this IEndpointConventionBuilder builder)
        => builder.WithMetadata(new AllowDeviceActorAttribute());
}

public sealed class DeviceActorBoundaryMiddleware
{
    private readonly RequestDelegate _next;

    public DeviceActorBoundaryMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var actor = ctx.GetActor();
        if (actor.IsDevice)
        {
            var endpoint = ctx.GetEndpoint();
            var allowsDevice = endpoint is not null && endpoint.Metadata.GetOrderedMetadata<AllowDeviceActorAttribute>().Any();
            if (!allowsDevice)
            {
                await ProblemResults.Forbidden("Device actors cannot access this endpoint.").ExecuteAsync(ctx);
                return;
            }
        }

        await _next(ctx);
    }
}
