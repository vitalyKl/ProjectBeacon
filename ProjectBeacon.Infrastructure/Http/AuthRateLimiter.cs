namespace ProjectBeacon.Infrastructure.Http;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

public static class AuthRateLimiter
{
    public static void Configure(RateLimiterOptions options, int permitLimit, int windowSeconds, int readPermitLimit = 60)
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

        static RateLimitPartition<string> Partition(HttpContext context, int permits, int seconds)
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromSeconds(seconds),
                AutoReplenishment = true,
                QueueLimit = 0
            });
        }
    }
}
