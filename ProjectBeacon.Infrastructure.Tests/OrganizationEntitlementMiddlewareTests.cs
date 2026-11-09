namespace ProjectBeacon.Infrastructure.Tests;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Entitlements;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Infrastructure.Http;

public sealed class OrganizationEntitlementMiddlewareTests
{
    [Fact]
    public async Task ActiveCoreEntitlement_AllowsTenantRequest()
    {
        var orgId = Guid.NewGuid();
        var tenant = Tenant(orgId);
        var service = new StubEntitlementService(Result.Ok(new EntitlementDecision(true)));
        var (context, next) = NewContext("/v1/tasks");
        var middleware = new OrganizationEntitlementMiddleware(next);

        await middleware.InvokeAsync(context, tenant, service);

        Assert.True(next.Called);
        Assert.Equal(orgId, service.OrgId);
        Assert.Equal(EntitlementFeatureKeys.Core, service.Requirement?.FeatureKey);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingCoreEntitlement_DeniesTenantRequestWithSafeProblemDetails()
    {
        var tenant = Tenant(Guid.NewGuid());
        var service = new StubEntitlementService(Result.Ok(new EntitlementDecision(false, "entitlement_missing")));
        var (context, next) = NewContext("/v1/tasks");
        var middleware = new OrganizationEntitlementMiddleware(next);

        await middleware.InvokeAsync(context, tenant, service);

        Assert.False(next.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var body = await ReadBodyAsync(context);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("entitlement_missing", json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/v1/auth/me")]
    [InlineData("/v1/auth/reset-password")]
    [InlineData("/v1/invites/token/accept")]
    [InlineData("/health")]
    public async Task RecoveryAndInviteRoutes_AreAvailableWithoutCoreEntitlement(string path)
    {
        var tenant = Tenant(Guid.NewGuid());
        var service = new StubEntitlementService(Result.Ok(new EntitlementDecision(false, "entitlement_missing")));
        var (context, next) = NewContext(path);
        var middleware = new OrganizationEntitlementMiddleware(next);

        await middleware.InvokeAsync(context, tenant, service);

        Assert.True(next.Called);
        Assert.False(service.Called);
    }

    [Fact]
    public async Task EntitlementServiceFailure_FailsClosedWith503()
    {
        var tenant = Tenant(Guid.NewGuid());
        var service = new StubEntitlementService(
            Result.Failure<EntitlementDecision>("database unavailable", ErrorKind.Unavailable));
        var (context, next) = NewContext("/v1/chat/sessions");

        await new OrganizationEntitlementMiddleware(next).InvokeAsync(context, tenant, service);

        Assert.False(next.Called);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.DoesNotContain("database unavailable", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestWithoutResolvedOrganization_DoesNotCallEntitlementService()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: false);
        var service = new StubEntitlementService(Result.Ok(new EntitlementDecision(false, "entitlement_missing")));
        var (context, next) = NewContext("/v1/tasks");

        await new OrganizationEntitlementMiddleware(next).InvokeAsync(context, tenant, service);

        Assert.True(next.Called);
        Assert.False(service.Called);
    }

    private static TenantContext Tenant(Guid orgId)
    {
        var tenant = new TenantContext();
        tenant.Assign(Guid.NewGuid(), orgId, unscoped: false);
        return tenant;
    }

    private static (DefaultHttpContext Context, NextHandler Next) NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        context.Response.Body = new MemoryStream();
        return (context, new NextHandler());
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private sealed class NextHandler
    {
        public bool Called { get; private set; }

        public RequestDelegate Delegate => _ =>
        {
            Called = true;
            return Task.CompletedTask;
        };

        public static implicit operator RequestDelegate(NextHandler handler) => handler.Delegate;
    }

    private sealed class StubEntitlementService(Result<EntitlementDecision> decision) : IEntitlementService
    {
        public bool Called { get; private set; }
        public Guid? OrgId { get; private set; }
        public EntitlementRequirement? Requirement { get; private set; }

        public Task<Result<EntitlementDecision>> EvaluateAsync(Guid orgId, EntitlementRequirement requirement, CancellationToken ct = default)
        {
            Called = true;
            OrgId = orgId;
            Requirement = requirement;
            return Task.FromResult(decision);
        }

        public Task<Result<OrganizationEntitlement?>> GetEffectiveAsync(Guid orgId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<OrganizationEntitlement?>> GetForOperatorAsync(AdminActor actor, Guid orgId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<OrganizationEntitlement>>> ListForOperatorAsync(AdminActor actor, Guid orgId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<OrganizationEntitlement>> GrantAsync(GrantOrganizationEntitlementRequest request, AdminActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result> RevokeAsync(Guid entitlementId, string reason, AdminActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<OrganizationEntitlement>> ConvertAlphaToProductionAsync(ConvertAlphaToProductionRequest request, AdminActor actor, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
