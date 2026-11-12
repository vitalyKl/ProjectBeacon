namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Domain.Enums;

public static class TestActorFactory
{
    public static ActorContext AdminFor(Guid projectId) =>
        new(
            ActorType.Human,
            UserId: Guid.NewGuid(),
            IsAdmin: true,
            TokenId: null,
            DeviceId: null,
            ProjectId: projectId,
            OrgId: null,
            Capabilities: ApiTokenCapability.None);
}
