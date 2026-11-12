namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Application.Persistence;
using Domain.Entities.Devices;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Read-only repository synchronization gate shared by pipeline preflight and launch handlers.</summary>
internal static class ProjectGitSyncPreflight
{
    internal sealed record State(
        bool Supported,
        bool InProgress,
        bool Blocked,
        int? Behind,
        string? Branch,
        DateTime? CheckedAt,
        GitSyncRefusalCode? ReasonCode);

    public static async Task<DaemonDevice?> SelectLatestOnlineDeviceAsync(
        IBeaconDb db,
        Guid projectId,
        DateTime utcNow,
        CancellationToken ct)
    {
        var deviceIds = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(runtime => runtime.ProjectId == projectId)
            .Select(runtime => runtime.DeviceId)
            .Distinct()
            .ToArrayAsync(ct);
        if (deviceIds.Length == 0)
            return null;

        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(device => deviceIds.Contains(device.Id))
            .ToListAsync(ct);

        return devices
            .Where(device => device.IsOnline(utcNow))
            .OrderByDescending(device => device.LastHeartbeatAt)
            .ThenBy(device => device.Id)
            .FirstOrDefault();
    }

    public static async Task<State> EvaluateAsync(
        IBeaconDb db,
        Guid projectId,
        DaemonDevice? device,
        CancellationToken ct)
    {
        if (device is null
            || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitSyncV1))
            return new State(false, false, false, null, null, null, null);

        var inProgress = await db.WorkstationCommands.AsNoTracking()
            .AnyAsync(command => command.ProjectId == projectId
                && command.DeviceId == device.Id
                && command.Kind == WorkstationCommandKind.GitSync
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running), ct);
        if (inProgress)
            return new State(true, true, true, null, null, null, null);

        var last = await db.WorkstationCommands.AsNoTracking()
            .Where(command => command.ProjectId == projectId
                && command.DeviceId == device.Id
                && command.Kind == WorkstationCommandKind.GitSync)
            .OrderByDescending(command => command.CreatedAt)
            .ThenByDescending(command => command.Id)
            .Select(command => new { command.Status, command.ResultJson, command.CompletedAt })
            .FirstOrDefaultAsync(ct);

        // A workstation that advertises GitSync must successfully verify the upstream before
        // its local checkout can be used as a new pipeline snapshot.
        if (last is null)
            return new State(true, false, true, null, null, null, null);

        if (last.Status != WorkstationCommandStatus.Succeeded
            || string.IsNullOrWhiteSpace(last.ResultJson))
            return new State(true, false, true, null, null, last.CompletedAt, GitSyncRefusalCode.FetchFailed);

        if (!TryReadSuccessfulResult(last.ResultJson, out var result))
            return new State(true, false, true, null, null, last.CompletedAt, GitSyncRefusalCode.FetchFailed);

        var blocked = result.Behind > 0
            || !result.Clean
            || !result.HasUpstream
            || result.Detached
            || result.Diverged
            || result.Outcome is not (GitSyncOutcome.UpToDate or GitSyncOutcome.FastForwarded);

        var reason = result.ReasonCode
            ?? (result.Detached ? GitSyncRefusalCode.Detached
                : !result.HasUpstream ? GitSyncRefusalCode.NoUpstream
                : !result.Clean ? GitSyncRefusalCode.DirtyWorkingTree
                : result.Diverged ? GitSyncRefusalCode.Diverged
                : result.Outcome is GitSyncOutcome.Failed or GitSyncOutcome.Refused
                    ? GitSyncRefusalCode.FetchFailed
                    : null);

        return new State(true, false, blocked, result.Behind, result.Branch, last.CompletedAt, reason);
    }

    private static bool TryReadSuccessfulResult(string json, out GitSyncResultDto result)
    {
        result = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadNonNegativeInt(root, "ahead", out _)
                || !TryReadNonNegativeInt(root, "behind", out _)
                || !TryReadBoolean(root, "clean", out _)
                || !TryReadBoolean(root, "detached", out _)
                || !TryReadBoolean(root, "hasUpstream", out _)
                || !TryReadBoolean(root, "diverged", out _)
                || !root.TryGetProperty("outcome", out var outcome)
                || outcome.ValueKind != JsonValueKind.Number
                || !outcome.TryGetInt32(out var outcomeValue)
                || !Enum.IsDefined(typeof(GitSyncOutcome), outcomeValue))
                return false;

            var parsed = JsonSerializer.Deserialize<GitSyncResultDto>(
                root.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed is null)
                return false;

            result = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadNonNegativeInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryReadBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (element.ValueKind == JsonValueKind.False)
            return true;
        return false;
    }
}
