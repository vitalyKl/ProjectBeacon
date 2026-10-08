namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;

/// <summary>
/// Marks an endpoint that a device actor may call. <see cref="DeviceActorBoundaryMiddleware"/> rejects device tokens on every other endpoint.
/// </summary>
public sealed class AllowDeviceActorAttribute : Attribute
{
}


/// <summary>
/// Blocks device actors unless the selected endpoint has <see cref="AllowDeviceActorAttribute"/>.
/// Device tokens only reach device routes.
/// </summary>
public sealed class DeviceActorBoundaryMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware with the next request delegate.</summary>
    public DeviceActorBoundaryMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// If the caller is a device and the endpoint is not marked <see cref="AllowDeviceActorAttribute"/>, writes 403 and does not call the rest of the pipeline.
    /// </summary>
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
