namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

public class CreateDeviceHandler : ICommandHandler<CreateDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateDeviceHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DaemonDeviceDto>> HandleAsync(CreateDeviceCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            return Result.Failure<DaemonDeviceDto>("Name is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Trim().Length > 200)
            return Result.Failure<DaemonDeviceDto>("Fingerprint is required (max 200 characters).");
        if (request.UserId == Guid.Empty)
            return Result.Failure<DaemonDeviceDto>("User is required.");

        await using var db = _dbFactory.CreateDbContext();
        var userExists = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == request.UserId, ct);
        if (!userExists)
            return Result.Failure<DaemonDeviceDto>("User not found.");

        var token = DeviceToken.Generate();
        var hash = DeviceToken.Hash(token);
        var prefix = DeviceToken.TokenPrefixOf(token);

        var existing = await db.DaemonDevices
            .FirstOrDefaultAsync(d => d.UserId == request.UserId && d.Fingerprint == request.Fingerprint.Trim() && d.RevokedAt == null, ct);

        if (existing is not null)
        {
            existing.RotateToken(hash, prefix);
            existing.Rename(request.Name);
            await db.SaveChangesAsync(ct);
            return Result.Ok(MapDevice(existing, token, DateTime.UtcNow));
        }

        var device = DaemonDevice.Create(request.Name, request.UserId, request.Fingerprint, hash, prefix);
        db.DaemonDevices.Add(device);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapDevice(device, token, DateTime.UtcNow));
    }

    internal static DaemonDeviceDto MapDevice(DaemonDevice device, string? token, DateTime utcNow) =>
        new(device.Id, device.Name, device.UserId, device.Fingerprint, device.TokenPrefix, token,
            device.LastHeartbeatAt, device.IsOnline(utcNow), device.ProbeJson, device.WorkstationJson,
            device.RevokedAt, device.CreatedAt);
}

public class ListDevicesHandler : ICommandHandler<ListDevicesCommand, Result<IList<DaemonDeviceDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListDevicesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DaemonDeviceDto>>> HandleAsync(ListDevicesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices
            .Where(d => d.UserId == command.Request.UserId)
            .OrderByDescending(d => d.LastHeartbeatAt)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<DaemonDeviceDto>)devices.Select(d => CreateDeviceHandler.MapDevice(d, null, now)).ToList());
    }
}

public class RevokeDeviceHandler : ICommandHandler<RevokeDeviceCommand, Result<bool>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RevokeDeviceHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(RevokeDeviceCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure<bool>("Device not found.");
        device.Revoke();
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}

public class HeartbeatDeviceHandler : ICommandHandler<HeartbeatDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public HeartbeatDeviceHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DaemonDeviceDto>> HandleAsync(HeartbeatDeviceCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<DaemonDeviceDto>("Device not found.");
        try
        {
            device.Heartbeat(command.Request.ProbeJson, command.Request.WorkstationJson);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<DaemonDeviceDto>(ex.Message);
        }
        RecordHostSample(db, device.Id, command.Request.ProbeJson);
        await db.SaveChangesAsync(ct);
        var stale = await db.DeviceHostSamples
            .Where(s => s.DeviceId == device.Id)
            .OrderByDescending(s => s.SampledAt)
            .Skip(60)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.DeviceHostSamples.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
        }
        return Result.Ok(CreateDeviceHandler.MapDevice(device, null, DateTime.UtcNow));
    }

    private static void RecordHostSample(BeaconDbContext db, Guid deviceId, string? probeJson)
    {
        var host = DeviceLlamaSwapProxy.ParseProbe(probeJson ?? "{}")?.Host;
        if (host is null)
            return;
        db.DeviceHostSamples.Add(DeviceHostSample.Create(
            deviceId,
            (host.SampledAt ?? DateTimeOffset.UtcNow).UtcDateTime,
            host.CpuPercent,
            host.RamUsedBytes,
            host.RamTotalBytes,
            host.Gpu?.Name,
            host.Gpu?.UtilizationPercent,
            host.Gpu?.MemoryUsedBytes,
            host.Gpu?.MemoryTotalBytes));
    }
}

public class EnqueueCommandHandler : ICommandHandler<EnqueueCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public EnqueueCommandHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(EnqueueCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Device not found.");
        if (!device.IsOnline(DateTime.UtcNow))
            return Result.Failure<WorkstationCommandDto>("Device is not connected.");

        if (command.Request.ProjectId is { } projectId)
        {
            var member = await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == command.Request.UserId, ct);
            if (!member)
                return Result.Failure<WorkstationCommandDto>("Project not found.");
        }

        var payloadJson = await InjectRootAsync(db, command.Request, device, ct);

        var queued = WorkstationCommand.Create(
            device.Id,
            command.Request.Kind,
            payloadJson,
            command.Request.ProjectId,
            command.Request.UserId);
        db.WorkstationCommands.Add(queued);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapCommand(queued));
    }

    private static readonly HashSet<WorkstationCommandKind> RootedKinds =
        [WorkstationCommandKind.ListDir, WorkstationCommandKind.ScanGguf, WorkstationCommandKind.InitProject, WorkstationCommandKind.ApplyOpencode];

    private static async Task<string?> InjectRootAsync(BeaconDbContext db, EnqueueCommandRequest request, DaemonDevice device, CancellationToken ct)
    {
        if (!RootedKinds.Contains(request.Kind))
            return request.PayloadJson;

        string? root = null;

        if (request.ProjectId is { } projectId)
        {
            var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.DeviceId == device.Id, ct);
            if (runtime is not null)
                root = runtime.LocalRoot;
        }

        if (root is null && !string.IsNullOrWhiteSpace(device.WorkstationJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(device.WorkstationJson);
                var propName = request.Kind == WorkstationCommandKind.ScanGguf ? "modelsRoot" : "projectsRoot";
                if (doc.RootElement.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
                    root = prop.GetString();
            }
            catch (JsonException) { }
        }

        if (root is null)
            return request.PayloadJson;

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
            return JsonSerializer.Serialize(new { root });

        try
        {
            var node = JsonNode.Parse(request.PayloadJson);
            if (node is JsonObject obj)
            {
                obj["root"] = root;
                return obj.ToJsonString();
            }
        }
        catch (JsonException) { }

        return request.PayloadJson;
    }

    internal static WorkstationCommandDto MapCommand(WorkstationCommand command) =>
        new(command.Id, command.DeviceId, command.ProjectId, command.RequestedByUserId, command.Kind, command.Status,
            command.PayloadJson, command.ResultJson, command.Error, command.CreatedAt, command.StartedAt, command.CompletedAt);
}

public class ClaimNextCommandHandler : ICommandHandler<ClaimNextCommandCommand, Result<WorkstationCommandDto?>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ClaimNextCommandHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto?>> HandleAsync(ClaimNextCommandCommand command, CancellationToken ct = default)
    {
        var wait = command.Request.Wait ?? TimeSpan.Zero;
        var deadline = DateTime.UtcNow + wait;
        do
        {
            await using var db = _dbFactory.CreateDbContext();
            var next = await db.WorkstationCommands
                .Where(c => c.DeviceId == command.Request.DeviceId && c.Status == WorkstationCommandStatus.Pending)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                next.Claim();
                await db.SaveChangesAsync(ct);
                return Result.Ok<WorkstationCommandDto?>(EnqueueCommandHandler.MapCommand(next));
            }

            if (DateTime.UtcNow >= deadline)
                return Result.Ok<WorkstationCommandDto?>(null);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok<WorkstationCommandDto?>(null);
            }
        } while (!ct.IsCancellationRequested);

        return Result.Ok<WorkstationCommandDto?>(null);
    }
}

public class CompleteCommandHandler : ICommandHandler<CompleteCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CompleteCommandHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(CompleteCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null || row.DeviceId != command.Request.DeviceId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        try
        {
            if (command.Request.Success)
                row.Succeed(command.Request.ResultJson);
            else
                row.Fail(string.IsNullOrWhiteSpace(command.Request.Error) ? "Command failed." : command.Request.Error);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<WorkstationCommandDto>(ex.Message);
        }
        await db.SaveChangesAsync(ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row));
    }
}

public class GetCommandHandler : ICommandHandler<GetCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetCommandHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(GetCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        return Result.Ok(EnqueueCommandHandler.MapCommand(row));
    }
}

public class AttachRuntimeHandler : ICommandHandler<AttachRuntimeCommand, Result<ProjectRuntimeDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AttachRuntimeHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectRuntimeDto>> HandleAsync(AttachRuntimeCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.LocalRoot) || request.LocalRoot.Trim().Length > 1000)
            return Result.Failure<ProjectRuntimeDto>("LocalRoot is required (max 1000 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == request.UserId, ct);
        if (!member)
            return Result.Failure<ProjectRuntimeDto>("Project not found.");

        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != request.UserId)
            return Result.Failure<ProjectRuntimeDto>("Device not found.");

        var existing = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ProjectId == request.ProjectId && r.DeviceId == request.DeviceId, ct);
        if (existing is not null)
        {
            existing.SetLocalRoot(request.LocalRoot);
            await db.SaveChangesAsync(ct);
            return Result.Ok(MapRuntime(existing, device, DateTime.UtcNow));
        }

        var runtime = ProjectRuntime.Create(request.ProjectId, request.DeviceId, request.LocalRoot);
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapRuntime(runtime, device, DateTime.UtcNow));
    }

    internal static ProjectRuntimeDto MapRuntime(ProjectRuntime runtime, DaemonDevice device, DateTime utcNow) =>
        new(runtime.Id, runtime.ProjectId, runtime.DeviceId, device.Name, device.IsOnline(utcNow),
            runtime.LocalRoot, runtime.CreatedAt, runtime.UpdatedAt);
}

public class ListRuntimesHandler : ICommandHandler<ListRuntimesCommand, Result<IList<ProjectRuntimeDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListRuntimesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ProjectRuntimeDto>>> HandleAsync(ListRuntimesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure<IList<ProjectRuntimeDto>>("Project not found.");

        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == command.Request.ProjectId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var deviceIds = runtimes.Select(r => r.DeviceId).ToList();
        var devices = await db.DaemonDevices.Where(d => deviceIds.Contains(d.Id)).ToListAsync(ct);
        var byId = devices.ToDictionary(d => d.Id);
        var now = DateTime.UtcNow;
        var dtos = new List<ProjectRuntimeDto>();
        foreach (var runtime in runtimes)
        {
            if (!byId.TryGetValue(runtime.DeviceId, out var device))
                continue;
            dtos.Add(AttachRuntimeHandler.MapRuntime(runtime, device, now));
        }
        return Result.Ok((IList<ProjectRuntimeDto>)dtos);
    }
}

public class DetachRuntimeHandler : ICommandHandler<DetachRuntimeCommand, Result<bool>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DetachRuntimeHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(DetachRuntimeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == command.Request.RuntimeId, ct);
        if (runtime is null)
            return Result.Failure<bool>("Runtime not found.");
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure<bool>("Runtime not found.");
        db.ProjectRuntimes.Remove(runtime);
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}

public class GetLlamaSwapConfigHandler : ICommandHandler<GetLlamaSwapConfigCommand, Result<LlamaSwapConfigDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetLlamaSwapConfigHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LlamaSwapConfigDto>> HandleAsync(GetLlamaSwapConfigCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<LlamaSwapConfigDto>("Device not found.");

        var projectIds = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.DeviceId == device.Id)
            .Select(r => r.ProjectId)
            .ToListAsync(ct);
        var backends = projectIds.Count == 0
            ? []
            : await db.LocalModelBackends.IgnoreQueryFilters()
                .Where(b => projectIds.Contains(b.ProjectId))
                .OrderBy(b => b.Name)
                .ToListAsync(ct);
        var specs = backends
            .Select(b => new LlamaSwapModelSpec(b.Name, b.LaunchCommand, b.ContextSize, b.Ttl, b.ExtraFlags, b.Concurrent))
            .ToList();
        return Result.Ok(new LlamaSwapConfigDto(LlamaSwapConfigGenerator.Generate(specs), 8080));
    }
}

public class ListHostSamplesHandler : ICommandHandler<ListHostSamplesCommand, Result<IList<DeviceHostSampleDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListHostSamplesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DeviceHostSampleDto>>> HandleAsync(ListHostSamplesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var devices = db.DaemonDevices.Where(d => d.UserId == command.Request.UserId && d.RevokedAt == null);
        if (command.Request.DeviceId is { } deviceId)
            devices = devices.Where(d => d.Id == deviceId);
        var deviceIds = await devices.Select(d => d.Id).ToListAsync(ct);
        if (deviceIds.Count == 0)
            return Result.Ok((IList<DeviceHostSampleDto>)[]);

        var samples = await db.DeviceHostSamples
            .Where(s => deviceIds.Contains(s.DeviceId))
            .OrderBy(s => s.SampledAt)
            .ToListAsync(ct);
        return Result.Ok((IList<DeviceHostSampleDto>)samples.Select(s => new DeviceHostSampleDto(
            s.Id, s.DeviceId, s.SampledAt, s.CpuPercent, s.RamUsedBytes, s.RamTotalBytes,
            s.GpuName, s.GpuUtilizationPercent, s.GpuMemoryUsedBytes, s.GpuMemoryTotalBytes)).ToList());
    }
}
