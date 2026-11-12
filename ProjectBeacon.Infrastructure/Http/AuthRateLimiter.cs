namespace ProjectBeacon.Infrastructure.Http;

using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Adds per-client-IP and authenticated-user rate limits for sensitive endpoints.
/// Rejection is HTTP 429 as problem+json.
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

        // The global limiter supplies an independent client-IP bucket. It only activates
        // on endpoints that opted into one of the named policies below.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var policyName = context.GetEndpoint()?.Metadata
                .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            var permits = policyName switch
            {
                "auth" => permitLimit,
                "auth-read" => readPermitLimit,
                "attachment-upload" => attachmentUploadPermitLimit,
                _ => 0
            };

            if (permits <= 0)
                return RateLimitPartition.GetNoLimiter("unlimited");

            var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var key = $"{policyName}:{remoteIp}";
            return RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => CreateOptions(permits, windowSeconds));
        });

        // The endpoint policy adds a second, independent bucket for authenticated users.
        // Anonymous requests continue to be protected by the client-IP global limiter.
        options.AddPolicy("auth", context => UserPartition(context, "auth", permitLimit, windowSeconds));
        options.AddPolicy("auth-read", context => UserPartition(context, "auth-read", readPermitLimit, windowSeconds));
        options.AddPolicy("attachment-upload", context =>
            UserPartition(context, "attachment-upload", attachmentUploadPermitLimit, windowSeconds));
    }

    private static RateLimitPartition<string> UserPartition(
        HttpContext context,
        string policyName,
        int permits,
        int seconds)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return RateLimitPartition.GetNoLimiter("anonymous");

        var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId) || userId == Guid.Empty)
            return RateLimitPartition.GetNoLimiter("anonymous");

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policyName}:user:{userId:N}",
            _ => CreateOptions(permits, seconds));
    }

    private static FixedWindowRateLimiterOptions CreateOptions(int permits, int seconds) => new()
    {
        PermitLimit = Math.Max(1, permits),
        Window = TimeSpan.FromSeconds(Math.Max(1, seconds)),
        AutoReplenishment = true,
        QueueLimit = 0
    };
}
