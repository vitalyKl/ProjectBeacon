namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.LlamaSwap;
using ModelSwapping;

public sealed partial class ClientLlamaSwap
{
    private async Task<bool> TryAdmitAsync(long need, CancellationToken ct)
    {
        if (need <= 0)
            return true;
        VramReading reading;
        try
        {
            reading = await VramChecker.ReadFreeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (reading.QueryFailed)
            return false;
        if (reading.FreeMb < 0)
            return true;
        return reading.FreeMb >= need;
    }

    private static long Footprint(LlamaServerBackend backend, LlamaSwapModelSpec spec)
    {
        if (backend.ActualVramMb > 0)
            return backend.ActualVramMb;
        if (backend.EstimatedVramMb > 0)
            return backend.EstimatedVramMb;
        return VramEstimate.FromSpec(spec);
    }

    private long ReservedExcept(string name)
    {
        long sum = 0;
        foreach (var backend in _own.Values)
        {
            if (backend.Name == name)
                continue;
            if (backend.State is not (BackendState.Ready or BackendState.Starting))
                continue;
            sum += backend.ActualVramMb > 0 ? backend.ActualVramMb : backend.EstimatedVramMb;
        }

        return sum;
    }

    private async Task StopOwnBackendsAsync(CancellationToken ct)
    {
        foreach (var backend in _own.Values)
        {
            await backend.StopAsync(ct);
            backend.Dispose();
            _coordinator.Deactivate(backend.Name);
        }
        _coordinator.UnregisterAll();
    }

    /// <summary>Remembers which swap-group model to keep resident; survives heartbeat ticks.</summary>
    internal void PreferSwapModel(string? name) => _desiredSwapModel = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    internal Task EnsureSwapModelAsync(string? name, CancellationToken ct)
    {
        PreferSwapModel(name);
        if (UseOwnSwapper && _ownLastYaml is not null)
            return TickOwnAsync(_ownLastYaml, _port, ct);
        return Task.CompletedTask;
    }

    private async Task TickOwnAsync(string yaml, int port, CancellationToken ct)
    {
        if (port > 0)
            _port = port;

        IReadOnlyList<LlamaSwapModelSpec> specs;
        try
        {
            specs = LlamaSwapConfigParser.Parse(yaml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new LlamaSwapStatusDto(false, false, null, null, null, $"config parse failed: {ex.Message}");
            return;
        }

        var swapSpec = specs.FirstOrDefault(s => !s.Concurrent && string.Equals(s.Name, _desiredSwapModel, StringComparison.OrdinalIgnoreCase))
            ?? specs.FirstOrDefault(s => !s.Concurrent);

        var desired = new Dictionary<string, (LlamaSwapModelSpec Spec, int Port)>(StringComparer.Ordinal);
        var residentIndex = 0;
        foreach (var spec in specs)
        {
            if (spec.Concurrent)
                desired[spec.Name] = (spec, ConcurrentPortBase + residentIndex++);
            else if (spec.Name == swapSpec?.Name)
                desired[spec.Name] = (spec, _port);
        }

        foreach (var name in _own.Keys.Where(k => !desired.ContainsKey(k)).ToList())
        {
            await _own[name].StopAsync(ct);
            _own[name].Dispose();
            _own.Remove(name);
            _coordinator.Unregister(name);
        }

        foreach (var (name, (spec, p)) in desired)
        {
            _coordinator.Register(name, spec.Concurrent);

            if (!spec.Concurrent && _coordinator.IsGroupBusy("swap"))
                continue;

            if (_own.TryGetValue(name, out var backend) && backend.Port != p)
            {
                await backend.StopAsync(ct);
                backend.Dispose();
                _coordinator.Deactivate(name);
                _own.Remove(name);
                backend = null;
            }

            var cost = backend is not null ? Footprint(backend, spec) : VramEstimate.FromSpec(spec);
            if (!await TryAdmitAsync(ReservedExcept(name) + cost, ct))
                continue;

            if (backend is null)
            {
                backend = new LlamaServerBackend
                {
                    Name = name,
                    Port = p,
                    SkipRealProcess = SkipRealProcess,
                    Log = Log
                };
                _own[name] = backend;
            }
            try
            {
                await backend.StartAsync(spec, ct);
                if (backend.State == BackendState.Ready)
                    _coordinator.TryActivate(name);
            }
            catch (Exception)
            {
                // StartAsync already recorded the fault; the next tick retries.
            }
        }

        Status = BuildOwnStatus();
    }

    private LlamaSwapStatusDto BuildOwnStatus()
    {
        if (_own.Count == 0)
            return new LlamaSwapStatusDto(false, false, null, null, null, null);

        var backends = _own.Values.ToArray();
        var faulted = backends.Where(b => b.State == BackendState.Faulted).ToArray();
        var ready = backends.Where(b => b.State == BackendState.Ready).ToArray();
        var available = ready.Length > 0 || backends.Any(b => b.State == BackendState.Starting);
        var healthy = ready.Length > 0 && faulted.Length == 0;

        var active = ready.FirstOrDefault(b => b.Port == _port) ?? ready.FirstOrDefault();
        var loadedModel = active?.Name;
        if (loadedModel != null && loadedModel != _lastLoaded)
        {
            _lastLoaded = loadedModel;
            _lastSwap = DateTime.UtcNow;
        }

        var reported = backends.Where(b => b.VramFootprintMb > 0).Sum(b => b.VramFootprintMb);
        var anyActual = backends.Any(b => b.ActualVramMb > 0);
        var memory = (anyActual ? "" : "est ") + reported.ToString(CultureInfo.InvariantCulture) + " MiB";
        var error = faulted.Length > 0 ? faulted[0].Error : null;

        var loadedModels = backends
            .OrderBy(b => b.Port)
            .Select(b => new LoadedModelStatus(b.Name, b.State.ToString().ToLowerInvariant()))
            .ToList();

        return new LlamaSwapStatusDto(available, healthy, loadedModel, memory, _lastSwap, error, loadedModels);
    }

}