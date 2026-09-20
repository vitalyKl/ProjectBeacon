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
            return new LlamaSwapStatusDto(false, false, null, null, null, "Start beacon client to see workstation load.");
        var parsed = ParseProbe(device.ProbeJson) ?? new LlamaSwapStatusDto(false, false, null, null, null,
            "Connected device has not reported llama-swap status.");
        return parsed with { DeviceName = device.Name };
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
        var now = DateTime.UtcNow;
        if (db.FilterProjectId is { } projectId && projectId != Guid.Empty)
        {
            var runtimeIds = await db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(r => r.ProjectId == projectId)
                .Select(r => r.DeviceId)
                .ToListAsync(ct);
            if (runtimeIds.Count > 0)
            {
                var runtimeDevices = await db.DaemonDevices.Where(d => runtimeIds.Contains(d.Id)).ToListAsync(ct);
                var onlineRuntime = runtimeDevices
                    .Where(d => d.IsOnline(now))
                    .OrderByDescending(d => d.LastHeartbeatAt)
                    .FirstOrDefault();
                if (onlineRuntime is not null)
                    return onlineRuntime;
            }

            var memberIds = await db.ProjectMembers.IgnoreQueryFilters()
                .Where(m => m.ProjectId == projectId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            if (memberIds.Count > 0)
            {
                var memberDevices = await db.DaemonDevices.Where(d => memberIds.Contains(d.UserId)).ToListAsync(ct);
                var onlineMember = memberDevices
                    .Where(d => d.IsOnline(now))
                    .OrderByDescending(d => d.LastHeartbeatAt)
                    .FirstOrDefault();
                if (onlineMember is not null)
                    return onlineMember;
            }
        }

        return null;
    }

    public static LlamaSwapStatusDto? ParseProbe(string probeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(probeJson) ? "{}" : probeJson);
            var host = doc.RootElement.TryGetProperty("hostLoad", out var hostEl)
                ? ParseHost(hostEl)
                : null;
            if (!doc.RootElement.TryGetProperty("llamaSwapStatus", out var status))
                return host is null ? null : new LlamaSwapStatusDto(false, false, null, null, null, null, null, host);
            var available = status.TryGetProperty("available", out var a) && a.GetBoolean();
            var healthy = status.TryGetProperty("healthy", out var h) && h.GetBoolean();
            var loaded = status.TryGetProperty("loadedModel", out var m) ? m.GetString() : null;
            var memory = status.TryGetProperty("memory", out var mem) ? mem.GetString() : null;
            DateTime? lastSwap = null;
            if (status.TryGetProperty("lastSwap", out var swap) && swap.ValueKind == JsonValueKind.String
                && DateTime.TryParse(swap.GetString(), out var parsed))
                lastSwap = parsed.ToUniversalTime();
            var error = status.TryGetProperty("error", out var err) ? err.GetString() : null;
            var models = ParseLoadedModels(status, loaded);
            return new LlamaSwapStatusDto(available, healthy, loaded, memory, lastSwap, error, models, host);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<LoadedModelStatus> ParseLoadedModels(JsonElement status, string? loaded)
    {
        if (status.TryGetProperty("loadedModels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<LoadedModelStatus>();
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                var state = el.TryGetProperty("state", out var s) ? s.GetString() : "ready";
                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(new LoadedModelStatus(name, state ?? "ready"));
            }
            if (list.Count > 0)
                return list;
        }
        if (string.IsNullOrWhiteSpace(loaded))
            return [];
        return loaded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new LoadedModelStatus(name, "ready"))
            .ToList();
    }

    private static HostLoadDto? ParseHost(JsonElement host)
    {
        if (host.ValueKind != JsonValueKind.Object)
            return null;
        double? cpu = host.TryGetProperty("cpuPercent", out var cpuEl) && cpuEl.TryGetDouble(out var cpuVal) ? cpuVal : null;
        long? ramUsed = host.TryGetProperty("ramUsedBytes", out var usedEl) && usedEl.TryGetInt64(out var usedVal) ? usedVal : null;
        long? ramTotal = host.TryGetProperty("ramTotalBytes", out var totalEl) && totalEl.TryGetInt64(out var totalVal) ? totalVal : null;
        DateTimeOffset? sampled = null;
        if (host.TryGetProperty("sampledAt", out var atEl) && atEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(atEl.GetString(), out var parsed))
            sampled = parsed;
        GpuLoadDto? gpu = null;
        if (host.TryGetProperty("gpu", out var gpuEl) && gpuEl.ValueKind == JsonValueKind.Object)
        {
            var name = gpuEl.TryGetProperty("name", out var n) ? n.GetString() : null;
            double? util = gpuEl.TryGetProperty("utilizationPercent", out var u) && u.TryGetDouble(out var uv) ? uv : null;
            long? gUsed = gpuEl.TryGetProperty("memoryUsedBytes", out var gu) && gu.TryGetInt64(out var guv) ? guv : null;
            long? gTotal = gpuEl.TryGetProperty("memoryTotalBytes", out var gt) && gt.TryGetInt64(out var gtv) ? gtv : null;
            if (name is not null || util is not null || gUsed is not null)
                gpu = new GpuLoadDto(name, util, gUsed, gTotal);
        }
        if (cpu is null && ramUsed is null && ramTotal is null && gpu is null)
            return null;
        return new HostLoadDto(cpu, ramUsed, ramTotal, gpu, sampled);
    }
}
