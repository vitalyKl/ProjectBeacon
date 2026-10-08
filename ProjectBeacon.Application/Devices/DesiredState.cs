namespace ProjectBeacon.Application.Devices;

using System.Text.Json.Nodes;
using Domain.Entities.Devices;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Bumps the desired revision on a user's devices or on the devices attached to a project so clients reconcile config.
/// </summary>
public static class DesiredState
{
    public static async Task BumpUserAsync(IBeaconDb db, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return;
        var devices = await db.DaemonDevices
            .Where(d => d.UserId == userId && d.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var device in devices)
            device.BumpDesired();
    }

    public static async Task BumpProjectAsync(IBeaconDb db, Guid projectId, CancellationToken ct)
    {
        if (projectId == Guid.Empty)
            return;
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);
        foreach (var runtime in runtimes)
            runtime.BumpConfig();
    }

    public static async Task BumpUserProjectsAsync(IBeaconDb db, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return;
        var deviceIds = await db.DaemonDevices
            .Where(d => d.UserId == userId && d.RevokedAt == null)
            .Select(d => d.Id)
            .ToListAsync(ct);
        if (deviceIds.Count == 0)
            return;
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => deviceIds.Contains(r.DeviceId))
            .ToListAsync(ct);
        foreach (var runtime in runtimes)
            runtime.BumpConfig();
    }

    public static bool TryStampWorkstation(string? payloadJson, out string stored, out string? error)
    {
        stored = "{}";
        error = null;
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson) is not JsonObject obj)
            {
                error = "payload must be a JSON object.";
                return false;
            }
            obj.Remove("revision");
            stored = obj.ToJsonString();
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            error = "payload must be valid JSON.";
            return false;
        }
    }

    public static string WithRevision(string json, long revision)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        node["revision"] = revision;
        return node.ToJsonString();
    }

    public static string ReconcilePayload(DaemonDevice device)
    {
        var root = new JsonObject { ["revision"] = device.DesiredRevision };
        if (!string.IsNullOrWhiteSpace(device.DesiredWorkstationJson)
            && device.DesiredWorkstationJson != "{}")
        {
            if (JsonNode.Parse(device.DesiredWorkstationJson) is JsonObject workstation)
                root["workstation"] = workstation;
        }
        return root.ToJsonString();
    }

    public static long? ReadRevision(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj && obj["revision"] is JsonValue value && value.TryGetValue<long>(out var revision))
                return revision;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }
}
