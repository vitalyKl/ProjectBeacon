namespace ProjectBeacon.Application.Tests;

using ProjectBeacon.Application.Authorization;
using Domain.Enums;

/// <summary>
/// Explicit actor used by pipeline behavior tests that exercise state transitions rather
/// than authorization. Authorization-specific tests use normal project members or scoped tokens.
/// </summary>
internal static class PipelineTestActor
{
    public static ActorContext AdminFor(Guid projectId) => new(
        ActorType.Human,
        Guid.NewGuid(),
        IsAdmin: true,
        TokenId: null,
        DeviceId: null,
        ProjectId: projectId,
        OrgId: null,
        Capabilities: ApiTokenCapability.None);
}
