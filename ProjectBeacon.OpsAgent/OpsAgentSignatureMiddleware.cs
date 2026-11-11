namespace ProjectBeacon.OpsAgent;

using System.Globalization;
using ProjectBeacon.Application.Operations;

public sealed class OpsAgentSignatureMiddleware(
    RequestDelegate next,
    OpsAgentOptions options,
    ReplayNonceStore nonceStore,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private const string TimestampHeader = "X-Beacon-Ops-Timestamp";
    private const string NonceHeader = "X-Beacon-Ops-Nonce";
    private const string SignatureHeader = "X-Beacon-Ops-Signature";

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/healthz", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/internal/v1/logs"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Get;
            return;
        }

        var timestamp = context.Request.Headers[TimestampHeader].ToString();
        var nonce = context.Request.Headers[NonceHeader].ToString();
        var signature = context.Request.Headers[SignatureHeader].ToString();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)
            || Math.Abs(timeProvider.GetUtcNow().ToUnixTimeSeconds() - unixSeconds)
                > MaximumClockSkew.TotalSeconds
            || nonce.Length != 32
            || nonce.Any(character => !Uri.IsHexDigit(character))
            || !PlatformLogRequestSignature.Verify(
                options.SharedSecret,
                timestamp,
                nonce,
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                signature))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!nonceStore.TryAccept(nonce, timeProvider.GetUtcNow()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
