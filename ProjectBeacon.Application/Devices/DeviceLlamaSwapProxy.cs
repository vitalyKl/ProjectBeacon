namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;

public sealed class DeviceLlamaSwapProxy : ILlamaSwapProxy
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly GetCommandHandler _getCommand;

    public DeviceLlamaSwapProxy(
        IDbContextFactory<BeaconDbContext> dbFactory,
        EnqueueCommandHandler enqueue,
        GetCommandHandler getCommand)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
        _getCommand = getCommand;
    }

    public async Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var device = await FindOnlineDeviceAsync(ct);
        if (device is null)
            return new LlamaSwapStatusDto(false, false, null, null, null, "No connected device is running llama-swap.");
        return ParseProbe(device.ProbeJson) ?? new LlamaSwapStatusDto(false, false, null, null, null,
            "Connected device has not reported llama-swap status.");
    }

    public Task<bool> ReloadAsync(CancellationToken ct = default) =>
        SendAsync(WorkstationCommandKind.ReloadProxy, ct);

    public Task<bool> UnloadAsync(CancellationToken ct = default) =>
        SendAsync(WorkstationCommandKind.UnloadProxy, ct);

    private async Task<bool> SendAsync(WorkstationCommandKind kind, CancellationToken ct)
    {
        var device = await FindOnlineDeviceAsync(ct);
        if (device is null)
            return false;
        var queued = await _enqueue.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(device.Id, device.UserId, kind, "{}", null)), ct);
        if (!queued.Success)
            return false;
        for (var i = 0; i < 40; i++)
        {
            var got = await _getCommand.HandleAsync(new GetCommandCommand(new GetCommandRequest(queued.Value!.Id, device.UserId)), ct);
            if (!got.Success)
                return false;
            if (got.Value!.Status == WorkstationCommandStatus.Succeeded)
                return true;
            if (got.Value.Status is WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
                return false;
            await Task.Delay(250, ct);
        }
        return false;
    }

    private async Task<Domain.Entities.Devices.DaemonDevice?> FindOnlineDeviceAsync(CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!db.FilterProjectId.HasValue || db.FilterProjectId == Guid.Empty)
            return null;
        var projectId = db.FilterProjectId.Value;
        var deviceIds = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId)
            .Select(r => r.DeviceId)
            .ToListAsync(ct);
        if (deviceIds.Count == 0)
            return null;
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices.Where(d => deviceIds.Contains(d.Id)).ToListAsync(ct);
        return devices
            .Where(d => d.IsOnline(now))
            .OrderByDescending(d => d.LastHeartbeatAt)
            .FirstOrDefault();
    }

    public static LlamaSwapStatusDto? ParseProbe(string probeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(probeJson) ? "{}" : probeJson);
            if (!doc.RootElement.TryGetProperty("llamaSwapStatus", out var status))
                return null;
            var available = status.TryGetProperty("available", out var a) && a.GetBoolean();
            var healthy = status.TryGetProperty("healthy", out var h) && h.GetBoolean();
            var loaded = status.TryGetProperty("loadedModel", out var m) ? m.GetString() : null;
            var memory = status.TryGetProperty("memory", out var mem) ? mem.GetString() : null;
            DateTime? lastSwap = null;
            if (status.TryGetProperty("lastSwap", out var swap) && swap.ValueKind == JsonValueKind.String
                && DateTime.TryParse(swap.GetString(), out var parsed))
                lastSwap = parsed.ToUniversalTime();
            var error = status.TryGetProperty("error", out var err) ? err.GetString() : null;
            return new LlamaSwapStatusDto(available, healthy, loaded, memory, lastSwap, error);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
