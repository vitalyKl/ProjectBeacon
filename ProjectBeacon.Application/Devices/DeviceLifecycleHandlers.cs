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
/// Enrolls a device and returns the raw token once. Name and fingerprint are required. Repeating a fingerprint rotates the token.
/// </summary>
public class CreateDeviceHandler : ICommandHandler<CreateDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector? _secrets;
    private readonly ITotp? _totp;

    public CreateDeviceHandler(IBeaconDbFactory dbFactory, ISecretProtector? secrets = null, ITotp? totp = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

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
        var gate = await TotpGate.RequireAsync(db, request.UserId, request.TotpCode, _secrets, _totp, ct);
        if (!gate.Success)
            return Result.Failure<DaemonDeviceDto>(gate.Error ?? "Authenticator code is required.");

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
            device.RevokedAt, device.CreatedAt, device.DesiredRevision, device.AppliedRevision);
}
/// <summary>
/// Lists the user's devices and whether each is online.
/// </summary>
public class ListDevicesHandler : ICommandHandler<ListDevicesCommand, Result<IList<DaemonDeviceDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListDevicesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DaemonDeviceDto>>> HandleAsync(ListDevicesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices
            .Where(d => d.UserId == command.Request.UserId && d.RevokedAt == null)
            .OrderByDescending(d => d.LastHeartbeatAt)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<DaemonDeviceDto>)devices.Select(d => CreateDeviceHandler.MapDevice(d, null, now)).ToList());
    }
}
/// <summary>
/// Revokes a device the user owns.
/// </summary>
public class RevokeDeviceHandler : ICommandHandler<RevokeDeviceCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RevokeDeviceHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(RevokeDeviceCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure("Device not found.");
        device.Revoke();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Records a device heartbeat. A revoked device fails.
/// </summary>
public class HeartbeatDeviceHandler : ICommandHandler<HeartbeatDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public HeartbeatDeviceHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
        await QueueReconcileAsync(db, device, ct);
        await QueueProjectAppliesAsync(db, device, ct);
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

    private static async Task QueueReconcileAsync(IBeaconDb db, DaemonDevice device, CancellationToken ct)
    {
        if (device.AppliedRevision >= device.DesiredRevision)
            return;
        var inflight = await db.WorkstationCommands.AnyAsync(c =>
            c.DeviceId == device.Id
            && c.Kind == WorkstationCommandKind.ReconcileDesired
            && (c.Status == WorkstationCommandStatus.Pending || c.Status == WorkstationCommandStatus.Running), ct);
        if (inflight)
            return;
        db.WorkstationCommands.Add(WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.ReconcileDesired,
            DesiredState.ReconcilePayload(device),
            requestedByUserId: device.UserId));
    }

    private static async Task QueueProjectAppliesAsync(IBeaconDb db, DaemonDevice device, CancellationToken ct)
    {
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.DeviceId == device.Id && r.AppliedConfigRevision < r.ConfigRevision)
            .ToListAsync(ct);
        if (runtimes.Count == 0)
            return;

        var pending = await db.WorkstationCommands
            .Where(c => c.DeviceId == device.Id
                && c.Kind == WorkstationCommandKind.ApplyOpencode
                && (c.Status == WorkstationCommandStatus.Pending || c.Status == WorkstationCommandStatus.Running))
            .Select(c => c.ProjectId)
            .ToListAsync(ct);
        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == device.UserId)
            .ToListAsync(ct);
        var dtos = backends.Select(ModelBackendMappers.ToDto).ToList();
        foreach (var runtime in runtimes)
        {
            if (pending.Contains(runtime.ProjectId))
                continue;
            var bindings = await db.RoleBindings.IgnoreQueryFilters()
                .Where(r => r.ProjectId == runtime.ProjectId)
                .ToListAsync(ct);
            var payload = OpencodePayload.FromBindings(bindings, dtos, runtime.ConfigRevision);
            db.WorkstationCommands.Add(WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.ApplyOpencode,
                payload,
                runtime.ProjectId,
                device.UserId));
        }
    }

    private static void RecordHostSample(IBeaconDb db, Guid deviceId, string? probeJson)
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
