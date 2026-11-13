namespace ProjectBeacon.Domain.Common;

using System.Text.Json;

/// <summary>
/// Capabilities advertised by a daemon heartbeat. Command producers use these to avoid
/// delivering a newly introduced wire enum to clients that cannot deserialize it.
/// </summary>
public static class WorkstationCapabilities
{
    public const string GitSyncV1 = "git-sync-v1";
    public const string GitLandV1 = "git-land-v1";

    public static bool Supports(string? probeJson, string capability)
    {
        if (string.IsNullOrWhiteSpace(probeJson) || string.IsNullOrWhiteSpace(capability))
            return false;

        try
        {
            using var document = JsonDocument.Parse(probeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("capabilities", out var capabilities)
                || capabilities.ValueKind != JsonValueKind.Array)
                return false;

            return capabilities.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), capability, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
