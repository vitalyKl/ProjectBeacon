namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain;

/// <summary>
/// Wire-level version and payload rules for workstation commands. Version 0 is the legacy
/// "field absent" marker, normalized to <see cref="CommandProtocol.CurrentVersion"/>; negative
/// versions and versions above <see cref="MaxSupportedVersion"/> are rejected.
/// </summary>
public static class CommandEnvelope
{
    public const int MaxSupportedVersion = CommandProtocol.CurrentVersion;

    public const string UnsupportedVersion = "unsupported command version.";
    public const string MalformedPayload = "command payload must be a valid JSON object.";

    /// <summary>Outcome of <see cref="NormalizeWireVersion"/>: an accepted version or an error.</summary>
    public readonly record struct NormalizedVersion(int Version, string? Error)
    {
        public bool IsOk => Error is null;

        public static NormalizedVersion Ok(int version) => new(version, null);

        public static NormalizedVersion Fail(string error) => new(0, error);
    }

    /// <summary>
    /// Maps a wire version to an accepted one. 0 (legacy absent) becomes the current version;
    /// 1..<see cref="MaxSupportedVersion"/> pass through; negative or above-max values are rejected.
    /// </summary>
    public static NormalizedVersion NormalizeWireVersion(int wireVersion)
    {
        if (wireVersion < 0)
            return NormalizedVersion.Fail($"command version must be non-negative, got {wireVersion}.");
        if (wireVersion == 0)
            return NormalizedVersion.Ok(CommandProtocol.CurrentVersion);
        if (wireVersion <= MaxSupportedVersion)
            return NormalizedVersion.Ok(wireVersion);
        return NormalizedVersion.Fail($"{UnsupportedVersion} got {wireVersion}.");
    }

    /// <summary>
    /// True when the payload is blank (treated as <c>{}</c>) or parses to a JSON object.
    /// </summary>
    public static bool HasValidPayload(string? payloadJson)
    {
        try
        {
            return JsonNode.Parse(NormalizedPayload(payloadJson)) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Normalizes a null or blank payload to <c>{}</c>.</summary>
    public static string NormalizedPayload(string? payloadJson) =>
        string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
}
