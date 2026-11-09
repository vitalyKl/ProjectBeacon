namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Admin;
using Application.Audit;
using Application.Authorization;
using Application.Auth;
using Application.Common;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Application.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
/// <summary>
/// Binds a project to a device and a local root. Root length is capped at 1000 characters.
/// </summary>
public class AttachRuntimeHandler : ICommandHandler<AttachRuntimeCommand, Result<RuntimeDiagnostics>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate? _adminGate;
    private readonly IAuditWriter? _auditWriter;

    public AttachRuntimeHandler(
        IBeaconDbFactory dbFactory,
        IAdminGate? adminGate = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
        _auditWriter = auditWriter;
    }

    public async Task<Result<RuntimeDiagnostics>> HandleAsync(AttachRuntimeCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.LocalRoot) || request.LocalRoot.Trim().Length > 1000)
            return Result.Failure<RuntimeDiagnostics>("LocalRoot is required (max 1000 characters).");
        if (!request.Actor.IsHuman || request.Actor.UserId is not { } userId)
            return Result.Failure<RuntimeDiagnostics>("Project not found.");

        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == userId, ct);
        if (!member)
        {
            if (_adminGate is null)
                return Result.Failure<RuntimeDiagnostics>("Project not found.");
            var operatorAccess = await _adminGate.RequireAsync(
                request.Actor, OperatorPermission.ProjectsManage, ct: ct);
            if (!operatorAccess.Success)
                return Result.Failure<RuntimeDiagnostics>("Project not found.");
        }

        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != userId)
            return Result.Failure<RuntimeDiagnostics>("Device not found.");

        var existing = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ProjectId == request.ProjectId && r.DeviceId == request.DeviceId, ct);
        if (_auditWriter is not null
            && (db.FilterProjectId != request.ProjectId || db.FilterOrgId is null))
            return Result.Failure<RuntimeDiagnostics>("Project not found.");

        if (existing is not null)
        {
            existing.SetLocalRoot(request.LocalRoot);
            _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                request.Actor,
                "project.runtime.updated",
                "ProjectRuntime",
                existing.Id.ToString(),
                db.FilterOrgId,
                request.ProjectId,
                detailsJson: """{"reasonCode":"local-root-updated"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Ok(MapDiagnostics(existing, device, DateTime.UtcNow));
        }

        var runtime = ProjectRuntime.Create(request.ProjectId, request.DeviceId, request.LocalRoot);
        db.ProjectRuntimes.Add(runtime);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            request.Actor,
            "project.runtime.attached",
            "ProjectRuntime",
            runtime.Id.ToString(),
            db.FilterOrgId,
            request.ProjectId,
            detailsJson: """{"newState":"Attached"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapDiagnostics(runtime, device, DateTime.UtcNow));
    }

    internal static RuntimeSummary MapSummary(ProjectRuntime runtime, DaemonDevice device, DateTime utcNow) =>
        new(runtime.Id, runtime.ProjectId, runtime.DeviceId, device.Name, device.IsOnline(utcNow),
            runtime.CreatedAt, runtime.UpdatedAt);

    internal static RuntimeDiagnostics MapDiagnostics(ProjectRuntime runtime, DaemonDevice device, DateTime utcNow) =>
        new(runtime.Id, runtime.ProjectId, runtime.DeviceId, device.Name, device.IsOnline(utcNow),
            runtime.LocalRoot, runtime.CreatedAt, runtime.UpdatedAt);
}
/// <summary>
/// Lists project runtimes the user can see.
/// </summary>
public class ListRuntimesHandler : ICommandHandler<ListRuntimesCommand, Result<IList<RuntimeSummary>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate? _adminGate;

    public ListRuntimesHandler(IBeaconDbFactory dbFactory, IAdminGate? adminGate = null)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
    }

    public async Task<Result<IList<RuntimeSummary>>> HandleAsync(ListRuntimesCommand command, CancellationToken ct = default)
    {
        if (!command.Request.Actor.IsHuman || command.Request.Actor.UserId is not { } userId)
            return Result.Failure<IList<RuntimeSummary>>("Project not found.");

        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == userId, ct);
        if (!member)
        {
            if (_adminGate is null)
                return Result.Failure<IList<RuntimeSummary>>("Project not found.");
            var operatorAccess = await _adminGate.RequireAsync(
                command.Request.Actor, OperatorPermission.OperationsRead, ct: ct);
            if (!operatorAccess.Success)
                return Result.Failure<IList<RuntimeSummary>>("Project not found.");
        }

        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == command.Request.ProjectId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var deviceIds = runtimes.Select(r => r.DeviceId).ToList();
        var devices = await db.DaemonDevices.Where(d => deviceIds.Contains(d.Id)).ToListAsync(ct);
        var byId = devices.ToDictionary(d => d.Id);
        var now = DateTime.UtcNow;
        var dtos = new List<RuntimeSummary>();
        foreach (var runtime in runtimes)
        {
            if (!byId.TryGetValue(runtime.DeviceId, out var device))
                continue;
            dtos.Add(AttachRuntimeHandler.MapSummary(runtime, device, now));
        }
        return Result.Ok((IList<RuntimeSummary>)dtos);
    }
}
/// <summary>
/// Removes a project runtime.
/// </summary>
public class DetachRuntimeHandler : ICommandHandler<DetachRuntimeCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate? _adminGate;
    private readonly IAuditWriter? _auditWriter;

    public DetachRuntimeHandler(
        IBeaconDbFactory dbFactory,
        IAdminGate? adminGate = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(DetachRuntimeCommand command, CancellationToken ct = default)
    {
        if (!command.Request.Actor.IsHuman || command.Request.Actor.UserId is not { } userId)
            return Result.Failure("Runtime not found.");

        await using var db = _dbFactory.CreateDbContext();
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == command.Request.RuntimeId, ct);
        if (runtime is null)
            return Result.Failure("Runtime not found.");
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == userId, ct);
        if (!member)
        {
            if (_adminGate is null)
                return Result.Failure("Runtime not found.");
            var operatorAccess = await _adminGate.RequireAsync(
                command.Request.Actor, OperatorPermission.ProjectsManage, ct: ct);
            if (!operatorAccess.Success)
                return Result.Failure("Runtime not found.");
        }
        if (_auditWriter is not null
            && (db.FilterProjectId != runtime.ProjectId || db.FilterOrgId is null))
            return Result.Failure("Runtime not found.");

        db.ProjectRuntimes.Remove(runtime);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            command.Request.Actor,
            "project.runtime.detached",
            "ProjectRuntime",
            runtime.Id.ToString(),
            db.FilterOrgId,
            runtime.ProjectId,
            detailsJson: """{"newState":"Detached"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Returns full runtime diagnostics (including the local root) only to a project owner/admin,
/// a system admin, or the owner of the runtime's device.
/// </summary>
public class GetRuntimeDiagnosticsHandler : ICommandHandler<GetRuntimeDiagnosticsCommand, Result<RuntimeDiagnostics>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate _adminGate;

    public GetRuntimeDiagnosticsHandler(IBeaconDbFactory dbFactory, IAdminGate? adminGate = null)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
    }

    public async Task<Result<RuntimeDiagnostics>> HandleAsync(GetRuntimeDiagnosticsCommand command, CancellationToken ct = default)
    {
        var runtimeId = command.Request.RuntimeId;
        var actor = command.Request.Actor;
        if (!actor.IsHuman || actor.UserId is not { } userId)
            return Result.Failure<RuntimeDiagnostics>("Runtime not found.");

        await using var db = _dbFactory.CreateDbContext();
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
            return Result.Failure<RuntimeDiagnostics>("Runtime not found.");

        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == userId, ct);
        var isSystemAdmin = false;
        if (_adminGate is not null)
        {
            var operatorAccess = await _adminGate.RequireAsync(
                actor, OperatorPermission.OperationsRead, ct: ct);
            isSystemAdmin = operatorAccess.Success;
        }
        if (!member && !isSystemAdmin)
            return Result.Failure<RuntimeDiagnostics>("Runtime not found.");

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == runtime.DeviceId, ct);
        if (device is null)
            return Result.Failure<RuntimeDiagnostics>("Runtime not found.");

        var isDeviceOwner = device.UserId == userId;
        var isProjectAdmin = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == userId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
        if (!isDeviceOwner && !isProjectAdmin && !isSystemAdmin)
            return Result.Forbidden<RuntimeDiagnostics>();

        return Result.Ok(AttachRuntimeHandler.MapDiagnostics(runtime, device, DateTime.UtcNow));
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
