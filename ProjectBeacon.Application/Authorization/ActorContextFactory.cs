namespace ProjectBeacon.Application.Authorization;

using System.Security.Claims;
using Domain.Enums;

public static class ActorContextFactory
{
    public const string ApiTokenAuthType = "ApiToken";
    public const string DeviceTokenAuthType = "DeviceToken";

    private const string TokenIdClaim = "token_id";
    private const string DeviceIdClaim = "device_id";
    private const string ProjectIdClaim = "project_id";
    private const string OrgIdClaim = "org_id";
    private const string CapabilitiesClaim = "capabilities";
    private const string IsAdminClaim = "isAdmin";
    private const string DeviceOwnerClaim = "device_owner_id";

    public static ActorContext FromPrincipal(ClaimsPrincipal? principal)
    {
        var identity = principal?.Identity;
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
