namespace ProjectBeacon.Infrastructure.Http;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using ProjectBeacon.Application.Authorization;

/// <summary>
/// Adds the identity half of rate limiting after bcn_ tokens have been validated.
/// The built-in limiter runs earlier and already protects the request by trusted client IP;
/// this second stage groups API tokens by their creator, falling back to token id for
/// service tokens without a human creator.
/// </summary>
public sealed class ApiTokenIdentityRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public ApiTokenIdentityRateLimiter(
        int permitLimit,
        int windowSeconds,
        int readPermitLimit = 60,
        int attachmentUploadPermitLimit = 10)
    {
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
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

            var actor = ActorContextFactory.FromPrincipal(context.User);
            if (permits <= 0 || !actor.IsApiToken)
                return RateLimitPartition.GetNoLimiter("not-an-api-token-policy");

            var subject = actor.UserId is { } creatorId
                ? $"creator:{creatorId:N}"
                : actor.TokenId is { } tokenId
                    ? $"token:{tokenId:N}"
                    : "invalid-token-identity";
            var key = $"{policyName}:{subject}";

            return RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, permits),
                    Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
                    AutoReplenishment = true,
                    QueueLimit = 0
                });
        });
    }

    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context, CancellationToken cancellationToken = default) =>
        _limiter.AcquireAsync(context, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
