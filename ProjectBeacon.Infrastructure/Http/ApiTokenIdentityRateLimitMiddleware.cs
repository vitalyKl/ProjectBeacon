namespace ProjectBeacon.Infrastructure.Http;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Enforces creator/token rate limits after API-token authentication. The earlier
/// ASP.NET rate-limiter middleware has already enforced the trusted client-IP limit.
/// </summary>
public sealed class ApiTokenIdentityRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiTokenIdentityRateLimiter limiter)
    {
        using var lease = await limiter.AcquireAsync(context, context.RequestAborted);
        if (!lease.IsAcquired)
        {
            await ProblemJson.WriteAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                "Too Many Requests",
                "https://tools.ietf.org/html/rfc6585#section-4",
                "Too many requests.",
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
