namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Tasks;
using ProjectBeacon.Domain.Enums;

public sealed class LandingAuthorizationPolicyTests
{
    [Fact]
    public void CanRequestLanding_AllowsUserBoundHuman()
    {
        var projectId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, null, null,
            ApiTokenCapability.None);

        Assert.True(LandingAuthorizationPolicy.CanRequestLanding(actor, projectId));
    }

    [Fact]
    public void CanRequestLanding_AllowsProjectScopedTaskWriteToken()
    {
        var projectId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId, null,
            ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite);

        Assert.True(LandingAuthorizationPolicy.CanRequestLanding(actor, projectId));
    }

    [Theory]
    [InlineData(ApiTokenCapability.TaskRead)]
    [InlineData(ApiTokenCapability.ContextRead)]
    [InlineData(ApiTokenCapability.Admin)]
    [InlineData(ApiTokenCapability.None)]
    public void CanRequestLanding_RejectsProjectTokensWithoutTaskWrite(ApiTokenCapability capabilities)
    {
        var projectId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId, null, capabilities);

        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(actor, projectId));
    }

    [Fact]
    public void CanRequestLanding_RejectsCrossProjectAndNonUserActors()
    {
        var projectId = Guid.NewGuid();
        var token = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null, Guid.NewGuid(), null,
            ApiTokenCapability.TaskWrite);
        var worker = new ActorContext(
            ActorType.Worker, null, false, null, null, projectId, null,
            ApiTokenCapability.None);
        var device = new ActorContext(
            ActorType.Device, null, false, null, Guid.NewGuid(), projectId, null,
            ApiTokenCapability.TaskWrite);

        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(token, projectId));
        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(worker, projectId));
        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(device, projectId));
        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(ActorContext.Anonymous, projectId));
        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(
            new ActorContext(ActorType.Human, null, false, null, null, null, null, ApiTokenCapability.None),
            projectId));
        Assert.False(LandingAuthorizationPolicy.CanRequestLanding(
            new ActorContext(ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId, null,
                ApiTokenCapability.TaskWrite),
            Guid.Empty));
    }
}
