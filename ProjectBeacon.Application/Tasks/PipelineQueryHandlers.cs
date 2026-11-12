namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Common;
using Application.Devices;
using Application.Common;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>Returns the task that has an active or waiting pipeline session.</summary>
public sealed class GetLivePipelineTaskHandler : ICommandHandler<GetLivePipelineTaskCommand, Result<Guid?>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetLivePipelineTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<Guid?>> HandleAsync(GetLivePipelineTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var taskId = await db.PipelineSessions.AsNoTracking()
            .Where(s => s.ProjectId == command.Request.ProjectId
                && (s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser))
            .OrderByDescending(s => s.LastActivityAt ?? s.LaunchedAt)
            .Select(s => (Guid?)s.TaskId)
            .FirstOrDefaultAsync(ct);
        return Result.Ok(taskId);
    }
}

/// <summary>
/// Returns subtasks, sessions, and verdicts for a task.
/// </summary>
public class GetPipelineHandler : ICommandHandler<GetPipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetPipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(GetPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var state = await PipelineSupport.StateAsync(db, task, ct);
        var preflight = await BuildPreflightAsync(db, task.Id, task.ProjectId, ct);
        var repositoryBehind = preflight.LastKnownRepositoryBehind is > 0;
        var repositorySyncInProgress = preflight.RepositorySyncInProgress;
        var repositoryBlocked = preflight.RepositorySyncBlocked || repositorySyncInProgress;
        return Result.Ok(state with
        {
            Preflight = preflight,
            CanStart = state.CanStart && !repositoryBlocked,
            StartBlockedReason = state.StartBlockedReason
                ?? (repositoryBehind
                    ? "PipelineRepositoryBehind"
                    : repositorySyncInProgress ? "PipelineRepositorySyncInProgress"
                    : preflight.RepositorySyncBlocked ? "PipelineRepositorySyncRequired" : null)
        });
    }

    private static async Task<PipelinePreflightDto> BuildPreflightAsync(
        IBeaconDb db, Guid taskId, Guid projectId, CancellationToken ct)
    {
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId).ToListAsync(ct);
        var deviceIds = runtimes.Select(r => r.DeviceId).Distinct().ToArray();
        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(d => deviceIds.Contains(d.Id)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var runtimeDevices = runtimes.Join(devices, r => r.DeviceId, d => d.Id,
            (runtime, device) => device).ToList();
        var selectedPipelineDevice = runtimeDevices
            .Where(device => device.IsOnline(now))
            .OrderByDescending(device => device.LastHeartbeatAt)
            .FirstOrDefault();
        var selectedPipelineDeviceId = selectedPipelineDevice?.Id;
        var pipelineGitSyncSupported = selectedPipelineDevice is not null
            && WorkstationCapabilities.Supports(
                selectedPipelineDevice.ProbeJson, WorkstationCapabilities.GitSyncV1);
        var nativeDevices = runtimeDevices
            .Where(device => IsNativeRuntime(device.WorkstationJson)).ToList();
        var hasNative = nativeDevices.Count > 0;
        var hasOnlineNative = nativeDevices.Any(device => device.IsOnline(now));

        var phases = await db.TaskPhases.Where(p => p.TaskId == taskId).ToListAsync(ct);
        var roleBindings = await db.RoleBindings.Where(b => b.ProjectId == projectId).ToListAsync(ct);
        var backendIds = (phases.Count > 0
                ? phases.Where(p => p.ModelBackendId.HasValue).Select(p => p.ModelBackendId!.Value)
                : roleBindings.Select(b => b.ModelBackendId))
            .Distinct().ToArray();
        var configuredBackends = await db.LocalModelBackends
            .Where(b => backendIds.Contains(b.Id)).Select(b => b.Id).ToListAsync(ct);
        var configured = configuredBackends.ToHashSet();

        bool HasModel(PipelineRole role)
        {
            if (phases.Count > 0)
            {
                var key = role switch
                {
                    PipelineRole.Planner => "understand",
                    PipelineRole.Actor => "do",
                    PipelineRole.Review => "check",
                    _ => string.Empty
                };
                var phase = phases.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
                return phase?.ModelBackendId is { } id && configured.Contains(id);
            }

            var binding = roleBindings.FirstOrDefault(b => b.Role == role);
            return binding is not null && configured.Contains(binding.ModelBackendId);
        }

        string? lastGitSyncResultJson = null;
        DateTime? lastGitSyncCompletedAt = null;
        var repositorySyncInProgress = false;
        if (selectedPipelineDeviceId is { } selectedDeviceId)
        {
            repositorySyncInProgress = await db.WorkstationCommands.AsNoTracking()
                .AnyAsync(command => command.ProjectId == projectId
                    && command.DeviceId == selectedDeviceId
                    && command.Kind == WorkstationCommandKind.GitSync
                    && (command.Status == WorkstationCommandStatus.Pending
                        || command.Status == WorkstationCommandStatus.Running), ct);

            var lastGitSync = await db.WorkstationCommands.AsNoTracking()
                .Where(command => command.ProjectId == projectId
                    && command.DeviceId == selectedDeviceId
                    && command.Kind == WorkstationCommandKind.GitSync
                    && command.Status == WorkstationCommandStatus.Succeeded
                    && command.ResultJson != null)
                .OrderByDescending(command => command.CompletedAt)
                .Select(command => new { command.ResultJson, command.CompletedAt })
                .FirstOrDefaultAsync(ct);
            lastGitSyncResultJson = lastGitSync?.ResultJson;
            lastGitSyncCompletedAt = lastGitSync?.CompletedAt;
        }

        int? lastKnownRepositoryBehind = null;
        string? lastKnownRepositoryBranch = null;
        GitSyncRefusalCode? lastKnownRepositoryReasonCode = null;
        var repositorySyncBlocked = false;
        if (!string.IsNullOrWhiteSpace(lastGitSyncResultJson))
        {
            try
            {
                using var gitSyncResult = JsonDocument.Parse(lastGitSyncResultJson);
                var root = gitSyncResult.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("behind", out var behindElement)
                    && behindElement.TryGetInt32(out var behind)
                    && behind >= 0)
                {
                    lastKnownRepositoryBehind = behind;
                    if (root.TryGetProperty("branch", out var branchElement)
                        && branchElement.ValueKind == JsonValueKind.String)
                        lastKnownRepositoryBranch = branchElement.GetString();

                    var hasUpstream = root.TryGetProperty("hasUpstream", out var upstreamElement)
                        && upstreamElement.ValueKind == JsonValueKind.True;
                    var detached = root.TryGetProperty("detached", out var detachedElement)
                        && detachedElement.ValueKind == JsonValueKind.True;
                    var diverged = root.TryGetProperty("diverged", out var divergedElement)
                        && divergedElement.ValueKind == JsonValueKind.True;
                    var failedOrRefused = root.TryGetProperty("outcome", out var outcomeElement)
                        && outcomeElement.ValueKind == JsonValueKind.Number
                        && outcomeElement.TryGetInt32(out var outcome)
                        && (outcome == (int)GitSyncOutcome.Failed
                            || outcome == (int)GitSyncOutcome.Refused);
                    var clean = root.TryGetProperty("clean", out var cleanElement)
                        && cleanElement.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("reasonCode", out var reasonElement)
                        && reasonElement.ValueKind == JsonValueKind.Number
                        && reasonElement.TryGetInt32(out var reason)
                        && Enum.IsDefined(typeof(GitSyncRefusalCode), reason))
                        lastKnownRepositoryReasonCode = (GitSyncRefusalCode)reason;
                    lastKnownRepositoryReasonCode ??= detached
                        ? GitSyncRefusalCode.Detached
                        : !hasUpstream
                            ? GitSyncRefusalCode.NoUpstream
                            : !clean
                                ? GitSyncRefusalCode.DirtyWorkingTree
                                : diverged
                                    ? GitSyncRefusalCode.Diverged
                                    : failedOrRefused ? GitSyncRefusalCode.FetchFailed : null;
                    repositorySyncBlocked = behind > 0 || !hasUpstream || detached || diverged
                        || !clean || failedOrRefused;
                }
            }
            catch (JsonException)
            {
                // A malformed or legacy result is treated as unknown, never as current.
            }
        }

        return new PipelinePreflightDto(
            hasNative,
            hasOnlineNative,
            HasModel(PipelineRole.Planner),
            HasModel(PipelineRole.Actor),
            HasModel(PipelineRole.Review),
            lastKnownRepositoryBehind,
            lastKnownRepositoryBranch,
            lastKnownRepositoryBehind is null ? null : lastGitSyncCompletedAt,
            selectedPipelineDeviceId,
            pipelineGitSyncSupported,
            repositorySyncInProgress,
            repositorySyncBlocked,
            lastKnownRepositoryReasonCode);
    }

    private static bool IsNativeRuntime(string? workstationJson)
    {
        if (string.IsNullOrWhiteSpace(workstationJson))
            return true;
        try
        {
            using var document = JsonDocument.Parse(workstationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            // Match the workstation settings UI: a missing runtime field means native.
            return !document.RootElement.TryGetProperty("agentRuntime", out var runtime)
                || (runtime.ValueKind == JsonValueKind.String
                    && string.Equals(runtime.GetString(), "native", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
