namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Persistence;
using Domain.Common;
using Domain.Entities.Devices;
using Domain.Entities.Tasks;
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

    internal sealed record PipelineWorkstationResolution(
        DaemonDevice? Device,
        bool HasNativeWorkstation,
        bool HasOnlineNativeWorkstation,
        string? BlockedReason);

    /// <summary>
    /// Resolves the one workstation that owns a task's persistent branch workspace.
    /// A saved pin always wins. Legacy tasks are inferred only when every claimed
    /// pipeline command identifies the same workstation; ambiguous history fails closed.
    /// </summary>
    public static async Task<PipelineWorkstationResolution> ResolveTaskWorkstationAsync(
        IBeaconDb db,
        TaskItem task,
        DateTime utcNow,
        CancellationToken ct)
    {
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(runtime => runtime.ProjectId == task.ProjectId)
            .ToListAsync(ct);
        var runtimeDeviceIds = runtimes.Select(runtime => runtime.DeviceId).Distinct().ToArray();
        var devices = runtimeDeviceIds.Length == 0
            ? new List<DaemonDevice>()
            : await db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => runtimeDeviceIds.Contains(device.Id))
                .ToListAsync(ct);
        var nativeDevices = devices
            .Where(device => IsNativeRuntime(device.WorkstationJson))
            .ToList();

        Guid? selectedDeviceId = task.PipelineDeviceId;
        if (selectedDeviceId is null)
        {
            var launchedCommands = await db.WorkstationCommands.IgnoreQueryFilters().AsNoTracking()
                .Where(command => command.ProjectId == task.ProjectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession
                    && command.StartedAt != null)
                .Select(command => new { command.DeviceId, command.PayloadJson })
                .ToListAsync(ct);
            var historicalDeviceIds = launchedCommands
                .Where(command => IsPipelineCommandForTask(command.PayloadJson, task.Id))
                .Select(command => command.DeviceId)
                .Distinct()
                .ToArray();

            if (historicalDeviceIds.Length > 1)
            {
                return new PipelineWorkstationResolution(
                    null,
                    nativeDevices.Count > 0 || historicalDeviceIds.Length > 0,
                    false,
                    "PipelineWorkstationHistoryAmbiguous");
            }

            if (historicalDeviceIds.Length == 1)
                selectedDeviceId = historicalDeviceIds[0];
        }

        if (selectedDeviceId is null)
        {
            var selected = nativeDevices
                .Where(device => device.IsOnline(utcNow))
                .OrderByDescending(device => device.LastHeartbeatAt)
                .ThenBy(device => device.Id)
                .FirstOrDefault();
            if (selected is null)
            {
                return new PipelineWorkstationResolution(
                    null,
                    nativeDevices.Count > 0,
                    false,
                    nativeDevices.Count > 0
                        ? "PipelineNativeWorkstationOffline"
                        : "PipelineNoNativeWorkstation");
            }

            selectedDeviceId = selected.Id;
        }

        var selectedDevice = devices.FirstOrDefault(device => device.Id == selectedDeviceId.Value);
        var isAttached = runtimeDeviceIds.Contains(selectedDeviceId.Value);
        if (!isAttached)
        {
            return new PipelineWorkstationResolution(
                selectedDevice,
                true,
                false,
                "PipelineWorkstationNotAttached");
        }

        if (selectedDevice is null)
        {
            return new PipelineWorkstationResolution(
                null,
                true,
                false,
                "PipelineWorkstationNotAttached");
        }

        if (!IsNativeRuntime(selectedDevice.WorkstationJson))
        {
            return new PipelineWorkstationResolution(
                selectedDevice,
                false,
                false,
                "PipelineWorkstationNotNative");
        }

        if (!selectedDevice.IsOnline(utcNow))
        {
            return new PipelineWorkstationResolution(
                selectedDevice,
                true,
                false,
                "PipelineNativeWorkstationOffline");
        }

        return new PipelineWorkstationResolution(selectedDevice, true, true, null);
    }

    /// <summary>Compare-and-set the immutable task workstation pin inside the caller's transaction.</summary>
    public static async Task<bool> TryPinTaskWorkstationAsync(
        IBeaconDb db,
        Guid taskId,
        Guid deviceId,
        CancellationToken ct)
    {
        if (deviceId == Guid.Empty)
            return false;

        var current = await db.Tasks.IgnoreQueryFilters()
            .Where(task => task.Id == taskId)
            .Select(task => task.PipelineDeviceId)
            .FirstOrDefaultAsync(ct);
        if (current == deviceId)
            return true;
        if (current is not null)
            return false;

        var updated = await db.Tasks.IgnoreQueryFilters()
            .Where(task => task.Id == taskId && task.PipelineDeviceId == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.PipelineDeviceId, (Guid?)deviceId), ct);
        if (updated == 1)
            return true;

        current = await db.Tasks.IgnoreQueryFilters()
            .Where(task => task.Id == taskId)
            .Select(task => task.PipelineDeviceId)
            .FirstOrDefaultAsync(ct);
        return current == deviceId;
    }

    private static bool IsPipelineCommandForTask(string payloadJson, Guid taskId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("taskId", out var taskIdElement)
                && taskIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(taskIdElement.GetString(), out var parsedTaskId)
                && parsedTaskId == taskId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string WorkstationBlockedMessage(string? reason) => reason switch
    {
        "PipelineWorkstationHistoryAmbiguous" =>
            "This task has pipeline runs on multiple workstations. Its local task worktree cannot be selected safely; reconcile or migrate the worktree explicitly before continuing.",
        "PipelineWorkstationNotAttached" =>
            "The workstation pinned to this task is no longer attached to the project. Reattach it or explicitly migrate the task worktree; automatic rerouting is disabled.",
        "PipelineWorkstationNotNative" =>
            "The workstation pinned to this task no longer advertises the native runtime. Restore its native runtime or explicitly migrate the task worktree.",
        "PipelineNativeWorkstationOffline" =>
            "The native workstation pinned to this task is offline.",
        "PipelineNoNativeWorkstation" =>
            "No native workstation is attached to this project.",
        _ => "The task's pinned pipeline workstation is unavailable."
    };

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
        if (device is null)
            return new State(false, false, false, null, null, null, null);

        // An already queued command may still mutate this checkout even if the latest
        // heartbeat no longer advertises GitSync. Check the active operation before
        // consulting capability metadata so it cannot slip past the launch barrier.
        var inProgress = await db.WorkstationCommands.AsNoTracking()
            .AnyAsync(command => command.ProjectId == projectId
                && command.DeviceId == device.Id
                && command.Kind == WorkstationCommandKind.GitSync
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running), ct);
        if (inProgress)
            return new State(
                WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitSyncV1),
                true,
                true,
                null,
                null,
                null,
                null);

        if (!WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitSyncV1))
            return new State(false, false, false, null, null, null, null);

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

        GitSyncRefusalCode? reasonCode;
        if (newerAttemptFailed)
            reasonCode = GitSyncRefusalCode.FetchFailed;
        else if (result.ReasonCode is { } reportedReason)
            reasonCode = reportedReason;
        else if (result.Detached)
            reasonCode = GitSyncRefusalCode.Detached;
        else if (!result.HasUpstream)
            reasonCode = GitSyncRefusalCode.NoUpstream;
        else if (result.Diverged)
            reasonCode = GitSyncRefusalCode.Diverged;
        else if ((result.Outcome is GitSyncOutcome.UpToDate or GitSyncOutcome.FastForwarded)
            && !result.Clean)
            reasonCode = GitSyncRefusalCode.DirtyWorkingTree;
        else if (inconsistentSuccess
            || (result.Outcome is GitSyncOutcome.Failed or GitSyncOutcome.Refused))
            reasonCode = GitSyncRefusalCode.FetchFailed;
        else
            reasonCode = null;

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
