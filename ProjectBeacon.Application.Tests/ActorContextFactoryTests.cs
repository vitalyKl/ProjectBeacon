namespace ProjectBeacon.Application.Tests;

using System.Security.Claims;
using Application.Authorization;
using Domain.Enums;

public sealed class ActorContextFactoryTests
{
    [Fact]
    public void FromPrincipal_Null_ReturnsAnonymous()
    {
        var ctx = ActorContextFactory.FromPrincipal(null);
        Assert.Equal(ActorContext.Anonymous, ctx);
    }

    [Fact]
    public void FromPrincipal_NoIdentity_ReturnsAnonymous()
    {
        var principal = new ClaimsPrincipal();
        var ctx = ActorContextFactory.FromPrincipal(principal);
        Assert.Equal(ActorContext.Anonymous, ctx);
    }

    [Fact]
    public void FromPrincipal_HumanWithClaims_ReturnsHuman()
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("isAdmin", "true"),
            new Claim("project_id", projectId.ToString()),
            new Claim("org_id", orgId.ToString())
        ], "Cookies"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.Human, ctx.Type);
        Assert.True(ctx.IsHuman);
        Assert.Equal(userId, ctx.UserId);
        Assert.True(ctx.IsAdmin);
        Assert.Null(ctx.TokenId);
        Assert.Null(ctx.DeviceId);
        Assert.Equal(projectId, ctx.ProjectId);
        Assert.Equal(orgId, ctx.OrgId);
        Assert.Equal(ApiTokenCapability.None, ctx.Capabilities);
    }

    [Fact]
    public void FromPrincipal_HumanWithoutUserId_ReturnsAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("isAdmin", "true")
        ], "Cookies"));

        var ctx = ActorContextFactory.FromPrincipal(principal);
        Assert.Equal(ActorContext.Anonymous, ctx);
    }

    [Fact]
    public void FromPrincipal_HumanNonAdmin()
    {
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("isAdmin", "false")
        ], "Cookies"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.Human, ctx.Type);
        Assert.Equal(userId, ctx.UserId);
        Assert.False(ctx.IsAdmin);
    }

    [Fact]
    public void FromPrincipal_ApiToken_ReturnsApiToken()
    {
        var tokenGuidId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("token_id", tokenGuidId.ToString()),
            new Claim("project_id", projectId.ToString()),
            new Claim("capabilities", "3")
        ], "ApiToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.ApiToken, ctx.Type);
        Assert.True(ctx.IsApiToken);
        Assert.Null(ctx.UserId);
        Assert.False(ctx.IsAdmin);
        Assert.Equal(tokenGuidId, ctx.TokenId);
        Assert.Null(ctx.DeviceId);
        Assert.Equal(projectId, ctx.ProjectId);
        Assert.Equal((ApiTokenCapability)3, ctx.Capabilities);
    }

    [Fact]
    public void FromPrincipal_DeviceToken_ReturnsDevice()
    {
        var deviceGuidId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("device_id", deviceGuidId.ToString())
        ], "DeviceToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.Device, ctx.Type);
        Assert.True(ctx.IsDevice);
        Assert.Null(ctx.UserId);
        Assert.Null(ctx.TokenId);
        Assert.Equal(deviceGuidId, ctx.DeviceId);
        Assert.Null(ctx.ProjectId);
        Assert.Equal(ApiTokenCapability.None, ctx.Capabilities);
    }

    [Fact]
    public void FromPrincipal_DeviceToken_WithOwner_ReturnsOwnerAsUserId()
    {
        var deviceGuidId = Guid.NewGuid();
        var ownerGuid = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("device_id", deviceGuidId.ToString()),
            new Claim("device_owner_id", ownerGuid.ToString())
        ], "DeviceToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.Device, ctx.Type);
        Assert.True(ctx.IsDevice);
        Assert.Equal(ownerGuid, ctx.UserId);
        Assert.Equal(deviceGuidId, ctx.DeviceId);
    }

    [Fact]
    public void FromPrincipal_DeviceToken_IgnoresNameIdentifier()
    {
        var deviceGuidId = Guid.NewGuid();
        var ownerGuid = Guid.NewGuid();
        var attackerGuid = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("device_id", deviceGuidId.ToString()),
            new Claim("device_owner_id", ownerGuid.ToString()),
            new Claim(ClaimTypes.NameIdentifier, attackerGuid.ToString())
        ], "DeviceToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ActorType.Device, ctx.Type);
        Assert.Equal(ownerGuid, ctx.UserId);
        Assert.NotEqual(attackerGuid, ctx.UserId);
    }

    [Fact]
    public void FromPrincipal_ApiTokenWithInvalidCapabilities_ReturnsNone()
    {
        var tokenGuidId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("token_id", tokenGuidId.ToString()),
            new Claim("capabilities", "not-a-number")
        ], "ApiToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Equal(ApiTokenCapability.None, ctx.Capabilities);
    }

    [Fact]
    public void FromPrincipal_GuidEmpty_TreatedAsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("token_id", Guid.Empty.ToString()),
            new Claim("project_id", Guid.Empty.ToString())
        ], "ApiToken"));

        var ctx = ActorContextFactory.FromPrincipal(principal);

        Assert.Null(ctx.TokenId);
        Assert.Null(ctx.ProjectId);
    }
}
