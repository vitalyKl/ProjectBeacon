namespace ProjectBeacon.Application.Authorization;

using System.Security.Claims;
using Domain.Enums;

/// <summary>Builds an <see cref="ActorContext"/> from a claims principal.</summary>
public static class ActorContextFactory
{
    /// <summary>Authentication type that selects an API-token actor.</summary>
    public const string ApiTokenAuthType = "ApiToken";
    /// <summary>Authentication type that selects a device actor.</summary>
    public const string DeviceTokenAuthType = "DeviceToken";

    private const string TokenIdClaim = "token_id";
    private const string DeviceIdClaim = "device_id";
    private const string ProjectIdClaim = "project_id";
    private const string OrgIdClaim = "org_id";
    private const string CapabilitiesClaim = "capabilities";
    private const string IsAdminClaim = "isAdmin";
    private const string DeviceOwnerClaim = "device_owner_id";

    /// <summary>
    /// Maps authentication type and claims onto an actor. <see cref="ApiTokenAuthType"/> and <see cref="DeviceTokenAuthType"/> select those kinds; any other identity is a human.
    /// A null principal, a missing identity, or a human with no user id is <see cref="ActorContext.Anonymous"/>. A device user id comes only from <c>device_owner_id</c>.
    /// </summary>
    public static ActorContext FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return ActorContext.Anonymous;

        var identity = principal.Identity;
        if (identity is null)
            return ActorContext.Anonymous;

        var type = identity.AuthenticationType switch
        {
            ApiTokenAuthType => ActorType.ApiToken,
            DeviceTokenAuthType => ActorType.Device,
            _ => ActorType.Human,
        };

        // Device actors carry the owner only as metadata (device_owner_id); the human
        // NameIdentifier claim must never be trusted as the device's acting identity.
        var userId = type == ActorType.Device
            ? ToGuid(principal.FindFirst(DeviceOwnerClaim)?.Value)
            : ToGuid(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        if (type == ActorType.Human && userId is null)
            return ActorContext.Anonymous;

        return type switch
        {
            ActorType.ApiToken => new ActorContext(
                type,
                userId,
                IsAdmin: false,
                TokenId: ToGuid(principal.FindFirst(TokenIdClaim)?.Value),
                DeviceId: null,
                ProjectId: ToGuid(principal.FindFirst(ProjectIdClaim)?.Value),
                OrgId: null,
                Capabilities: ToCapabilities(principal.FindFirst(CapabilitiesClaim)?.Value)),
            ActorType.Device => new ActorContext(
                type,
                userId,
                IsAdmin: false,
                TokenId: null,
                DeviceId: ToGuid(principal.FindFirst(DeviceIdClaim)?.Value),
                ProjectId: null,
                OrgId: null,
                Capabilities: ApiTokenCapability.None),
            _ => new ActorContext(
                ActorType.Human,
                userId,
                IsAdmin: ToBool(principal.FindFirst(IsAdminClaim)?.Value),
                TokenId: null,
                DeviceId: null,
                ProjectId: ToGuid(principal.FindFirst(ProjectIdClaim)?.Value),
                OrgId: ToGuid(principal.FindFirst(OrgIdClaim)?.Value),
                Capabilities: ApiTokenCapability.None),
        };
    }

    private static Guid? ToGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static bool ToBool(string? value) =>
        bool.TryParse(value, out var b) && b;

    private static ApiTokenCapability ToCapabilities(string? value) =>
        long.TryParse(value, out var bits) ? (ApiTokenCapability)bits : ApiTokenCapability.None;
}
