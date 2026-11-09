namespace ProjectBeacon.Web.Authentication;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Fails closed for browser form posts that create or destroy the Control Plane session.
/// Accepts only the exact same origin; sibling subdomains (same-site) are not trusted.
/// </summary>
public static class ControlFormOriginGuard
{
    public static bool IsAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.HasFormContentType)
            return false;

        var originValue = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(originValue)
            || string.Equals(originValue, "null", StringComparison.OrdinalIgnoreCase)
            || originValue.Contains(','))
            return false;

        if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin)
            || !Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host.Value}", UriKind.Absolute, out var expected))
            return false;

        if (!string.Equals(origin.GetLeftPart(UriPartial.Authority),
                expected.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        return string.IsNullOrWhiteSpace(fetchSite)
            || string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
    }
}
