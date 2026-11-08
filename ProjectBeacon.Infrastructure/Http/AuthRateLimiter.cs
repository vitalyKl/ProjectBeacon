namespace ProjectBeacon.Infrastructure.Http;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Adds the auth, read, and attachment-upload rate-limit policies. Rejection is HTTP 429 as problem+json.
/// </summary>
public static class AuthRateLimiter
{
    public static void Configure(
        RateLimiterOptions options,
        int permitLimit,
        int windowSeconds,
        int readPermitLimit = 60,
        int attachmentUploadPermitLimit = 10)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (ctx, ct) => new ValueTask(ProblemJson.WriteAsync(
            ctx.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "Too Many Requests",
            "https://tools.ietf.org/html/rfc6585#section-4",
            "Too many requests.",
            ct));
        options.AddPolicy("auth", context => Partition(context, permitLimit, windowSeconds));
        options.AddPolicy("auth-read", context => Partition(context, readPermitLimit, windowSeconds));
        options.AddPolicy("attachment-upload", context => Partition(context, attachmentUploadPermitLimit, windowSeconds));

        static RateLimitPartition<string> Partition(HttpContext context, int permits, int seconds)
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permits),
                Window = TimeSpan.FromSeconds(Math.Max(1, seconds)),
                AutoReplenishment = true,
                QueueLimit = 0
            });
        }
    }
}
