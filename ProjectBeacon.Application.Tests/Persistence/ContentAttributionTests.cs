namespace ProjectBeacon.Application.Tests.Persistence;

using Application.Authorization;
using Application.Persistence;
using Domain.Enums;

public sealed class ContentAttributionTests
{
    [Fact]
    public void FromActor_UsesTrustedActorKindAndIdentifier()
    {
        var userId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var human = ContentAttribution.FromActor(new ActorContext(
            ActorType.Human, userId, false, null, null, projectId, null, ApiTokenCapability.None));
        Assert.Equal("Human", human.Type);
        Assert.Equal(userId.ToString("D"), human.Id);

        var token = ContentAttribution.FromActor(new ActorContext(
            ActorType.ApiToken, null, false, tokenId, null, projectId, null, ApiTokenCapability.TaskWrite));
        Assert.Equal("ApiToken", token.Type);
        Assert.Equal(tokenId.ToString("D"), token.Id);

        var agent = ContentAttribution.FromActor(new ActorContext(
            ActorType.ApiToken, null, false, tokenId, null, projectId, null, ApiTokenCapability.TaskWrite),
            "subtask-42");
        Assert.Equal("Agent", agent.Type);
        Assert.Equal("subtask-42", agent.Id);

        var device = ContentAttribution.FromActor(new ActorContext(
            ActorType.Device, null, false, null, deviceId, projectId, null, ApiTokenCapability.None));
        Assert.Equal("Device", device.Type);
        Assert.Equal(deviceId.ToString("D"), device.Id);

        var worker = ContentAttribution.FromActor(new ActorContext(
            ActorType.Worker, null, false, null, deviceId, projectId, null, ApiTokenCapability.None));
        Assert.Equal("Worker", worker.Type);
        Assert.Equal(deviceId.ToString("D"), worker.Id);
    }

    [Fact]
    public void FromActor_NullActor_DoesNotInventAttribution()
    {
        var attribution = ContentAttribution.FromActor(null);

        Assert.Null(attribution.Type);
        Assert.Null(attribution.Id);
    }
}
