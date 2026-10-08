namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Auth;
using Application.Common;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
/// <summary>
/// Binds a project to a device and a local root. Root length is capped at 1000 characters.
/// </summary>
public class AttachRuntimeHandler : ICommandHandler<AttachRuntimeCommand, Result<ProjectRuntimeDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public AttachRuntimeHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
/// <summary>
/// Lists project runtimes the user can see.
/// </summary>
public class ListRuntimesHandler : ICommandHandler<ListRuntimesCommand, Result<IList<ProjectRuntimeDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListRuntimesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
/// <summary>
/// Removes a project runtime.
/// </summary>
public class DetachRuntimeHandler : ICommandHandler<DetachRuntimeCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DetachRuntimeHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DetachRuntimeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == command.Request.RuntimeId, ct);
        if (runtime is null)
            return Result.Failure("Runtime not found.");
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure("Runtime not found.");
        db.ProjectRuntimes.Remove(runtime);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Renders llama-swap YAML for the device from the user's backends.
/// </summary>
public class GetLlamaSwapConfigHandler : ICommandHandler<GetLlamaSwapConfigCommand, Result<LlamaSwapConfigDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ILlamaSwapCatalog _catalog;

    public GetLlamaSwapConfigHandler(IBeaconDbFactory dbFactory, ILlamaSwapCatalog catalog)
    {
        _dbFactory = dbFactory;
        _catalog = catalog;
    }

    public async Task<Result<LlamaSwapConfigDto>> HandleAsync(GetLlamaSwapConfigCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<LlamaSwapConfigDto>("Device not found.");

        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == device.UserId && b.BackendType == ModelBackendType.LlamaCpp)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var specs = backends
            .Select(b => new LlamaSwapModelBinding(b.Name, b.LaunchCommand, b.ContextSize, b.Ttl, b.ExtraFlags, b.Concurrent))
            .ToList();
        return Result.Ok(new LlamaSwapConfigDto(_catalog.GenerateYaml(specs), _catalog.Port));
    }
}
/// <summary>
/// Returns recent CPU, memory, and GPU samples reported by the user's devices.
/// </summary>
public class ListHostSamplesHandler : ICommandHandler<ListHostSamplesCommand, Result<IList<DeviceHostSampleDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListHostSamplesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
