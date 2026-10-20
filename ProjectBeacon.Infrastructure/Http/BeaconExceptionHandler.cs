namespace ProjectBeacon.Infrastructure.Http;

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
/// <summary>
/// Turns unhandled exceptions on the API prefix into problem+json. Development may include the exception message.
/// A <see cref="BadHttpRequestException"/> is a client fault: it returns its own status code
/// (400, 413, 415) with a matching title, and is logged as a warning, not as a server error.
/// </summary>
public static class BeaconExceptionHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IApplicationBuilder UseBeaconExceptionHandler(this IApplicationBuilder app, string? pathPrefix = null)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(BeaconExceptionHandler));
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

                var badRequest = exception as BadHttpRequestException;
                if (badRequest is not null)
                    logger.LogWarning("Bad request ({Status}) for {Method} {Path}", badRequest.StatusCode, context.Request.Method, context.Request.Path.Value);
                else if (exception is not null)
                    logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path.Value);

                if (context.Response.HasStarted)
                    return;

                var status = badRequest?.StatusCode ?? StatusCodes.Status500InternalServerError;
                var problem = new Dictionary<string, object?>
                {
                    ["type"] = status switch
                    {
                        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                        StatusCodes.Status413PayloadTooLarge => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
                        StatusCodes.Status415UnsupportedMediaType => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
                        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1"
                    },
                    ["title"] = status switch
                    {
                        StatusCodes.Status400BadRequest => "Bad Request",
                        StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
                        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
                        _ => "Internal Server Error"
                    },
                    ["status"] = status,
                    ["traceId"] = ProblemJson.TraceId(context)
                };

                if (exception is not null && isDevelopment)
                {
                    problem["detail"] = exception.Message;
                    problem["exceptionType"] = exception.GetType().Name;
                }

                context.Response.StatusCode = status;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
            }));
    }
}
