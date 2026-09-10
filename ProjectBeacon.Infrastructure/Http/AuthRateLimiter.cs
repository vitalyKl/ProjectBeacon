namespace ProjectBeacon.Infrastructure.Http;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

public static class AuthRateLimiter
{
    public static void Configure(RateLimiterOptions options, int permitLimit, int windowSeconds)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("auth", context =>
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                AutoReplenishment = true,
                QueueLimit = 0
            });
        });
    }
}
