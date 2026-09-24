namespace ProjectBeacon.Infrastructure.Http;

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class BeaconExceptionHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IApplicationBuilder UseBeaconExceptionHandler(this IApplicationBuilder app, string? pathPrefix = null)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(BeaconExceptionHandler).FullName);
        var isDevelopment = app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

        return app.UseExceptionHandler(errorApp =>
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

                if (pathPrefix is not null && !context.Request.Path.StartsWithSegments(pathPrefix))
                {
                    if (exception is not null)
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    return;
                }

                if (exception is not null)
                    logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path.Value);

                if (context.Response.HasStarted)
                    return;

                var problem = new Dictionary<string, object?>
                {
                    ["type"] = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                    ["title"] = "An unexpected error occurred.",
                    ["status"] = StatusCodes.Status500InternalServerError
                };

                if (exception is not null && isDevelopment)
                {
                    problem["detail"] = exception.Message;
                    problem["exceptionType"] = exception.GetType().Name;
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
            }));
    }
}
