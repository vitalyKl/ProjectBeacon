namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
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
        var repositorySyncInProgress = preflight.RepositorySyncInProgress;
        return Result.Ok(state with
        {
            Preflight = preflight,
            CanStart = state.CanStart && !repositorySyncInProgress,
            StartBlockedReason = repositorySyncInProgress
                ? "PipelineRepositorySyncInProgress"
                : state.StartBlockedReason
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
            .Where(device => device.IsOnline(now)
                && ProjectGitSyncPreflight.IsNativeRuntime(device.WorkstationJson))
            .OrderByDescending(device => device.LastHeartbeatAt)
            .ThenBy(device => device.Id)
            .FirstOrDefault();
        var selectedPipelineDeviceId = selectedPipelineDevice?.Id;
        var repositorySync = await ProjectGitSyncPreflight.EvaluateAsync(
            db, projectId, selectedPipelineDevice, ct);
        var nativeDevices = runtimeDevices
            .Where(device => ProjectGitSyncPreflight.IsNativeRuntime(device.WorkstationJson)).ToList();
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

        return new PipelinePreflightDto(
            hasNative,
            hasOnlineNative,
            HasModel(PipelineRole.Planner),
            HasModel(PipelineRole.Actor),
            HasModel(PipelineRole.Review),
            repositorySync.Behind,
            repositorySync.Branch,
            repositorySync.CheckedAt,
            selectedPipelineDeviceId,
            repositorySync.Supported,
            repositorySync.InProgress,
            repositorySync.Blocked,
            repositorySync.ReasonCode);
    }


}
