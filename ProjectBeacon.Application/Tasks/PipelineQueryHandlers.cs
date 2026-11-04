namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Application.Common;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
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
        return Result.Ok(state with { Preflight = preflight });
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
        var nativeDevices = runtimes.Join(devices, r => r.DeviceId, d => d.Id, (_, device) => device)
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

        return new PipelinePreflightDto(
            hasNative,
            hasOnlineNative,
            HasModel(PipelineRole.Planner),
            HasModel(PipelineRole.Actor),
            HasModel(PipelineRole.Review));
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
