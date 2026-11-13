namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Persistence;
using Domain.Common;
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
            .Where(device => device.IsOnline(utcNow) && IsNativeRuntime(device.WorkstationJson))
            .OrderByDescending(device => device.LastHeartbeatAt)
            .ThenBy(device => device.Id)
            .FirstOrDefault();
    }

    /// <summary>Matches the pipeline's native-workstation eligibility rule.</summary>
    public static bool IsNativeRuntime(string? workstationJson)
    {
        if (string.IsNullOrWhiteSpace(workstationJson))
            return true;
        try
        {
            using var document = JsonDocument.Parse(workstationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var runtimeProperties = document.RootElement.EnumerateObject()
                .Where(property => string.Equals(
                    property.Name, "agentRuntime", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return runtimeProperties.Length == 0
                || runtimeProperties.All(property => property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), "native", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
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

        // A queued or running GitSync can mutate this checkout while the pipeline is
        // snapshotting it. Only commands for the exact pipeline workstation are relevant:
        // another workstation owns a different local checkout.
        var inProgress = await db.WorkstationCommands.AsNoTracking()
            .AnyAsync(command => command.ProjectId == projectId
                && command.DeviceId == device.Id
                && command.Kind == WorkstationCommandKind.GitSync
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running), ct);
        if (inProgress)
            return new State(true, true, true, null, null, null, null);

        // Cancelled commands are often superseded by a replacement Pull. They must not
        // erase the last usable repository observation.
        var attempts = await db.WorkstationCommands.AsNoTracking()
            .Where(command => command.ProjectId == projectId
                && command.DeviceId == device.Id
                && command.Kind == WorkstationCommandKind.GitSync
                && command.Status != WorkstationCommandStatus.Cancelled)
            .OrderByDescending(command => command.CreatedAt)
            .ThenByDescending(command => command.Id)
            .Take(20)
            .Select(command => new
            {
                command.Status,
                command.ResultJson,
                command.CompletedAt,
                command.CreatedAt
            })
            .ToListAsync(ct);

        if (attempts.Count == 0)
            return new State(true, false, false, null, null, null, GitSyncRefusalCode.NeverSynced);

        // A transport/worker failure does not invalidate the last successful Git result.
        // Prefer the newest parseable command result and retain that older result when a
        // newer command failed before it could report repository state.
        var verificationIndex = -1;
        GitSyncResultDto? result = null;
        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            if (attempt.Status != WorkstationCommandStatus.Succeeded
                || string.IsNullOrWhiteSpace(attempt.ResultJson))
                continue;

            if (TryReadSuccessfulResult(attempt.ResultJson, out var parsed))
            {
                verificationIndex = index;
                result = parsed;
                break;
            }
        }

        if (verificationIndex < 0 || result is null)
        {
            GitSyncRefusalCode? reason = attempts[0].Status is WorkstationCommandStatus.Failed
                or WorkstationCommandStatus.Succeeded
                    ? GitSyncRefusalCode.FetchFailed
                    : null;
            return new State(true, false, false, null, null, attempts[0].CompletedAt, reason);
        }

        var verification = attempts[verificationIndex];
        var newerAttemptFailed = attempts.Take(verificationIndex).Any(attempt =>
            attempt.Status == WorkstationCommandStatus.Failed
            || attempt.Status == WorkstationCommandStatus.Succeeded
                && (string.IsNullOrWhiteSpace(attempt.ResultJson)
                    || !TryReadSuccessfulResult(attempt.ResultJson, out _)));

        // GitSync verifies one concrete LocalRoot. If the runtime was created or its root
        // changed after the result completed, keep this as a warning rather than a launch gate.
        var runtimeVersionAt = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(runtime => runtime.ProjectId == projectId && runtime.DeviceId == device.Id)
            .Select(runtime => (DateTime?)(runtime.UpdatedAt ?? runtime.CreatedAt))
            .FirstOrDefaultAsync(ct);

        var sameHead = string.Equals(result.HeadBefore, result.HeadAfter, StringComparison.OrdinalIgnoreCase);
        var inconsistentSuccess = result.Outcome switch
        {
            GitSyncOutcome.UpToDate => !sameHead,
            GitSyncOutcome.FastForwarded => sameHead || result.Behind != 0 || !result.Clean,
            _ => false
        };

        var reasonCode = newerAttemptFailed
            ? GitSyncRefusalCode.FetchFailed
            : result.ReasonCode
                ?? (result.Detached ? GitSyncRefusalCode.Detached
                    : !result.HasUpstream ? GitSyncRefusalCode.NoUpstream
                    : result.Diverged ? GitSyncRefusalCode.Diverged
                    : (result.Outcome is GitSyncOutcome.UpToDate or GitSyncOutcome.FastForwarded)
                        && !result.Clean
                        ? GitSyncRefusalCode.DirtyWorkingTree
                    : inconsistentSuccess || result.Outcome is GitSyncOutcome.Failed or GitSyncOutcome.Refused
                        ? GitSyncRefusalCode.FetchFailed
                        : null);

        if (runtimeVersionAt is null
            || verification.CompletedAt is null
            || runtimeVersionAt.Value >= verification.CompletedAt.Value)
            reasonCode = GitSyncRefusalCode.RuntimeChanged;

        // Behind, dirty, detached, divergent, missing-upstream, and failed-fetch states are
        // surfaced to the UI as warnings. They do not make a local pipeline un-runnable.
        return new State(true, false, false, result.Behind, result.Branch, verification.CompletedAt, reasonCode);
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
                || !TryReadBoolean(root, "hasUpstream", out var hasUpstream)
                || !TryReadBoolean(root, "diverged", out _)
                || !TryReadNullableString(root, "branch", out var branchName)
                || !TryReadNullableString(root, "upstream", out var upstream)
                || !TryReadNullableString(root, "headBefore", out var headBefore)
                || !TryReadNullableString(root, "headAfter", out var headAfter)
                || !root.TryGetProperty("outcome", out var outcome)
                || !TryReadOutcome(outcome)
                || !root.TryGetProperty("reasonCode", out var reasonCode)
                || !TryReadReasonCode(reasonCode)
                || (hasUpstream && string.IsNullOrWhiteSpace(upstream)))
                return false;

            var outcomeValue = ReadOutcome(outcome);
            var detachedValue = root.GetProperty("detached").GetBoolean();
            if (outcomeValue is GitSyncOutcome.UpToDate or GitSyncOutcome.FastForwarded)
            {
                if ((!detachedValue && string.IsNullOrWhiteSpace(branchName))
                    || !IsCommitId(headBefore)
                    || !IsCommitId(headAfter))
                    return false;
            }

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            var parsed = JsonSerializer.Deserialize<GitSyncResultDto>(root.GetRawText(), options);
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

    private static bool TryReadOutcome(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out var numeric)
                && Enum.IsDefined(typeof(GitSyncOutcome), numeric);
        return element.ValueKind == JsonValueKind.String
            && Enum.TryParse<GitSyncOutcome>(element.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed);
    }

    private static GitSyncOutcome ReadOutcome(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number
            ? (GitSyncOutcome)element.GetInt32()
            : Enum.Parse<GitSyncOutcome>(element.GetString()!, ignoreCase: true);

    private static bool TryReadReasonCode(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out var numeric)
                && Enum.IsDefined(typeof(GitSyncRefusalCode), numeric);
        return element.ValueKind == JsonValueKind.String
            && Enum.TryParse<GitSyncRefusalCode>(element.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed);
    }

    private static bool IsCommitId(string? value) =>
        value is { Length: 40 or 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private static bool TryReadNullableString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
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
