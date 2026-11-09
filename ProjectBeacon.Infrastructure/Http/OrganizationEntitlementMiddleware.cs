namespace ProjectBeacon.Infrastructure.Http;

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.Application.Entitlements;
using ProjectBeacon.Application.Persistence;

/// <summary>
/// Enforces the baseline organization entitlement after tenant isolation has resolved the
/// authenticated request's organization. Authentication, password recovery and invite
/// redemption remain available so an operator can restore access.
/// </summary>
public sealed class OrganizationEntitlementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenant,
        IEntitlementService entitlements)
    {
        if (ShouldSkip(context, tenant))
        {
            await next(context);
            return;
        }

        var orgId = tenant.OrgId!.Value;
        var evaluation = await entitlements.EvaluateAsync(
            orgId,
            new EntitlementRequirement(EntitlementFeatureKeys.Core),
            context.RequestAborted);

        if (!evaluation.Success)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Organization access could not be verified",
                "The organization's access could not be verified. Try again later.",
                "entitlement_check_failed");
            return;
        }

        if (evaluation.Value is not { Allowed: true })
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Organization access required",
                "This organization does not have an active ProjectBeacon entitlement. Contact the platform operator.",
                evaluation.Value?.ReasonCode ?? "entitlement_required");
            return;
        }

        await next(context);
    }

    private static bool ShouldSkip(HttpContext context, ITenantContext tenant)
    {
        var path = context.Request.Path;
        return context.User.Identity?.IsAuthenticated != true
            || tenant.Unscoped
            || tenant.OrgId is not { } orgId
            || orgId == Guid.Empty
            || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/v1/version")
            || path.StartsWithSegments("/v1/auth")
            || path.StartsWithSegments("/v1/invites");
    }

    private static async Task WriteProblemAsync(
        HttpContext context, int status, string title, string detail, string code)
    {
        context.Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = status == StatusCodes.Status403Forbidden
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.4"
                : "https://tools.ietf.org/html/rfc9110#section-15.6.4"
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        await Results.Problem(problem).ExecuteAsync(context);
    }
}
