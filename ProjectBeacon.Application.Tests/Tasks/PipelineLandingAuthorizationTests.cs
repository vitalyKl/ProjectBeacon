namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Tasks;
using Domain.Enums;

public sealed class PipelineLandingAuthorizationTests
{
    [Fact]
    public void RevertAuthorization_AllowsHumanOrProjectBoundTaskWriteToken()
    {
        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var human = new ActorContext(
            ActorType.Human, userId, false, null, null, projectId, null, ApiTokenCapability.None);
        var taskWriteToken = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            projectId, null, ApiTokenCapability.TaskWrite | ApiTokenCapability.TaskRead);

        Assert.True(LandingAuthorizationPolicy.CanRequestRevert(human, projectId, automatic: false));
        Assert.True(LandingAuthorizationPolicy.CanRequestRevert(taskWriteToken, projectId, automatic: false));
    }

    [Fact]
    public void RevertAuthorization_RejectsUnboundOrInsufficientTokensAndNonUserActors()
    {
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var readOnlyToken = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            projectId, null, ApiTokenCapability.TaskRead);
        var crossProjectToken = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            otherProjectId, null, ApiTokenCapability.TaskWrite);
        var unboundToken = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            null, null, ApiTokenCapability.TaskWrite);
        var device = new ActorContext(
            ActorType.Device, null, false, null, Guid.NewGuid(),
            projectId, null, ApiTokenCapability.None);
        var anonymousHuman = new ActorContext(
            ActorType.Human, null, false, null, null,
            projectId, null, ApiTokenCapability.None);

        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(readOnlyToken, projectId, automatic: false));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(crossProjectToken, projectId, automatic: false));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(unboundToken, projectId, automatic: false));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(device, projectId, automatic: false));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(anonymousHuman, projectId, automatic: false));
    }

    [Fact]
    public void RevertAuthorization_KeepsAutomaticRevertsWorkerOnlyAndProjectBound()
    {
        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var worker = new ActorContext(
            ActorType.Worker, null, false, null, null,
            projectId, null, ApiTokenCapability.None);
        var otherProjectWorker = worker with { ProjectId = Guid.NewGuid() };
        var taskWriteToken = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            projectId, null, ApiTokenCapability.TaskWrite);

        Assert.True(LandingAuthorizationPolicy.CanRequestRevert(worker, projectId, automatic: true));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(otherProjectWorker, projectId, automatic: true));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(taskWriteToken, projectId, automatic: true));
        Assert.False(LandingAuthorizationPolicy.CanRequestRevert(worker, projectId, automatic: false));
    }
}
