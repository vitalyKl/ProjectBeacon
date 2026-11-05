namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Owns long-running services started by one native agent session. Commands are launched
/// directly from argv in the same clean per-session environment as run_command.
/// </summary>
internal sealed class NativeServiceManager : IAsyncDisposable
{
    private const int MaxLogBytes = 16 * 1024 * 1024;
    private const int DefaultReadyTimeoutSeconds = 30;
    private const int MaxReadyTimeoutSeconds = 120;
    private const int MaxLogPageSize = 250;
    private static readonly string RegistryRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectBeacon", "managed-services");
    private static readonly Regex DefaultReadyRegex = new(
        @"Now listening|Application started|\bready\b|\bListening on\b|\bLocal:\s*https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ManagedService>> _sessions =
        new(StringComparer.Ordinal);
    private static int _reaperRan;
    private volatile bool _disposed;

    public NativeServiceManager()
    {
        if (Interlocked.Exchange(ref _reaperRan, 1) == 0)
            ReapStaleServices();
    }

    public static JsonObject StartToolDefinition() => Tool("service_start",
        "Start a long-running project service with argv (no shell). Waits for a TCP port or startup output to indicate readiness. Returns serviceId and status.",
        new JsonObject
        {
            ["argv"] = ArgvSchema(),
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Optional short label for this service." },
            ["port"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535, ["description"] = "Optional local TCP port to poll for readiness." },
            ["readyPattern"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200, ["description"] = "Optional bounded regular expression matching startup output. Defaults to common .NET/Node/web-server ready messages." },
            ["readyTimeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxReadyTimeoutSeconds, ["description"] = "Readiness timeout, default 30 seconds." }
        },
        ["argv"]);

    public static JsonObject StatusToolDefinition() => Tool("service_status",
        "Return the current state and process id of services started by this agent session. Omit serviceId to list all.",
        new JsonObject
        {
            ["serviceId"] = new JsonObject { ["type"] = "string", ["description"] = "Optional serviceId returned by service_start." }
        },
        []);

    public static JsonObject LogsToolDefinition() => Tool("service_logs",
        "Read a page of the complete output log for a service. Use nextOffset to continue.",
        new JsonObject
        {
            ["serviceId"] = new JsonObject { ["type"] = "string", ["description"] = "serviceId returned by service_start." },
            ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Line offset, default 0." },
            ["maxLines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxLogPageSize, ["description"] = "Lines to read, default 100." }
        },
        ["serviceId"]);

    public static JsonObject StopToolDefinition() => Tool("service_stop",
        "Stop a service and its entire child process tree.",
        new JsonObject
        {
            ["serviceId"] = new JsonObject { ["type"] = "string", ["description"] = "serviceId returned by service_start." }
        },
        ["serviceId"]);

    public async Task<McpToolResult> StartAsync(
        string sessionId,
        string workingDirectory,
        JsonObject arguments,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (arguments["argv"] is not JsonArray argvNode
            || argvNode.Count == 0
            || argvNode.Any(item => item is not JsonValue value || value.GetValueKind() != JsonValueKind.String))
            return new McpToolResult(true, "service_start rejected: argv must be a non-empty array of strings.");

        var argv = argvNode.Select(item => item!.GetValue<string>()).ToArray();
        if (!CommandExecutionPolicy.TryValidate(argv, workingDirectory, out var validationError))
            return new McpToolResult(true, "service_start rejected: " + validationError);

        var port = ReadInt(arguments, "port");
        if (port is < 1 or > 65535)
            return new McpToolResult(true, "service_start rejected: port must be between 1 and 65535.");

        var readyTimeout = ReadInt(arguments, "readyTimeoutSeconds") ?? DefaultReadyTimeoutSeconds;
        if (readyTimeout < 1 || readyTimeout > MaxReadyTimeoutSeconds)
            return new McpToolResult(true, $"service_start rejected: readyTimeoutSeconds must be between 1 and {MaxReadyTimeoutSeconds}.");

        var patternText = ReadString(arguments, "readyPattern");
        if (patternText is { Length: > 200 })
            return new McpToolResult(true, "service_start rejected: readyPattern cannot exceed 200 characters.");
        Regex? readyRegex = null;
        if (!string.IsNullOrWhiteSpace(patternText))
        {
            try { readyRegex = new Regex(patternText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return new McpToolResult(true, "service_start rejected: readyPattern is not a valid regular expression."); }
        }

        var sessionDirectory = NativeCommandRunner.SessionHomePath(sessionId);
        var logDirectory = Path.Combine(sessionDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var serviceId = Guid.NewGuid().ToString("N");
        var logPath = NativeCommandRunner.RunLogPath(sessionId, serviceId);
        Directory.CreateDirectory(RegistryRoot);
        var registryPath = Path.Combine(RegistryRoot, serviceId + ".json");
        if (!File.Exists(logPath))
            await File.WriteAllTextAsync(logPath, string.Empty, new UTF8Encoding(false), ct);
        var label = ReadString(arguments, "name");
        if (string.IsNullOrWhiteSpace(label))
            label = Path.GetFileName(argv[0]);

        ProcessStartInfo startInfo;
        try
        {
            startInfo = NativeCommandRunner.CreateStartInfo(workingDirectory, sessionId, argv);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new McpToolResult(true, $"service_start could not resolve the executable safely: {ex.Message}");
        }
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var service = new ManagedService(serviceId, sessionId, label!, logPath, registryPath, process, port, readyRegex ?? DefaultReadyRegex);
        process.OutputDataReceived += (_, eventArgs) => service.Append("OUT", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => service.Append("ERR", eventArgs.Data);
        process.Exited += (_, _) => service.MarkExited();

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return new McpToolResult(true, "service_start failed: the process did not start.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            return new McpToolResult(true, $"service_start failed: {ex.GetType().Name}.");
        }

        ProcessJob.TryAssign(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!service.PersistRegistry())
        {
            await service.StopAsync(CancellationToken.None);
            return new McpToolResult(true, "service_start failed: could not register the service process for crash recovery.");
        }

        var session = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ManagedService>(StringComparer.Ordinal));
        if (!session.TryAdd(serviceId, service))
        {
            await service.StopAsync(CancellationToken.None);
            return new McpToolResult(true, "service_start failed: could not register the service.");
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(readyTimeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    service.MarkExited();
                    return new McpToolResult(true, JsonSerializer.Serialize(new
                    {
                        serviceId,
                        name = label,
                        status = "exited-before-ready",
                        exitCode = TryExitCode(process),
                        logId = serviceId,
                        outputTail = service.OutputTail
                    }));
                }

                if (service.MatchesReadyPattern() || (port is { } targetPort && await IsPortOpenAsync(targetPort, ct)))
                {
                    service.MarkReady();
                    return new McpToolResult(false, JsonSerializer.Serialize(new
                    {
                        serviceId,
                        name = label,
                        status = "ready",
                        processId = SafeProcessId(process),
                        port,
                        logId = serviceId,
                        durationMs = service.DurationMs,
                        outputTail = service.OutputTail
                    }));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }

            await service.StopAsync(CancellationToken.None);
            return new McpToolResult(true, JsonSerializer.Serialize(new
            {
                serviceId,
                name = label,
                status = "readiness-timeout",
                port,
                logId = serviceId,
                durationMs = service.DurationMs,
                outputTail = service.OutputTail
            }));
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            throw;
        }
    }

    public McpToolResult Status(string sessionId, JsonObject arguments)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        var requested = ReadString(arguments, "serviceId");
        if (requested is not null)
        {
            if (session is null || !session.TryGetValue(requested, out var one))
                return new McpToolResult(true, "service_status: serviceId was not found in this session.");
            one.RefreshExitState();
            return new McpToolResult(false, JsonSerializer.Serialize(one.ToSnapshot()));
        }

        var services = session?.Values.ToArray() ?? [];
        foreach (var service in services)
            service.RefreshExitState();
        return new McpToolResult(false, JsonSerializer.Serialize(new
        {
            services = services.OrderBy(service => service.StartedAt).Select(service => service.ToSnapshot()).ToArray()
        }));
    }

    public async Task<McpToolResult> LogsAsync(string sessionId, JsonObject arguments, CancellationToken ct)
    {
        var serviceId = ReadString(arguments, "serviceId")?.Trim();
        if (string.IsNullOrWhiteSpace(serviceId) || !Regex.IsMatch(serviceId, @"^[a-fA-F0-9]{32}$"))
            return new McpToolResult(true, "service_logs rejected: serviceId is invalid.");

        var offset = ReadInt(arguments, "offset") ?? 0;
        var maxLines = ReadInt(arguments, "maxLines") ?? 100;
        if (offset < 0 || maxLines < 1 || maxLines > MaxLogPageSize)
            return new McpToolResult(true, $"service_logs rejected: offset must be non-negative and maxLines must be 1..{MaxLogPageSize}.");

        var path = NativeCommandRunner.RunLogPath(sessionId, serviceId);
        if (!File.Exists(path))
            return new McpToolResult(true, "service_logs: no log exists for this service in the current session.");

        var lines = new List<string>(maxLines + 1);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var current = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (current++ < offset)
                continue;
            lines.Add(line);
            if (lines.Count > maxLines)
                break;
        }

        var hasMore = lines.Count > maxLines;
        if (hasMore)
            lines.RemoveAt(lines.Count - 1);
        return new McpToolResult(false, JsonSerializer.Serialize(new
        {
            serviceId,
            offset,
            lines,
            nextOffset = hasMore ? offset + lines.Count : (int?)null,
            endOfLog = !hasMore
        }));
    }

    public async Task<McpToolResult> StopAsync(string sessionId, JsonObject arguments, CancellationToken ct)
    {
        var serviceId = ReadString(arguments, "serviceId")?.Trim();
        if (string.IsNullOrWhiteSpace(serviceId))
            return new McpToolResult(true, "service_stop rejected: serviceId is required.");

        var session = _sessions.GetValueOrDefault(sessionId);
        if (session is null || !session.TryGetValue(serviceId, out var service))
            return new McpToolResult(true, "service_stop: serviceId was not found in this session.");

        await service.StopAsync(ct);
        return new McpToolResult(false, JsonSerializer.Serialize(service.ToSnapshot()));
    }

    public async Task StopAllAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var services))
            return;

        foreach (var service in services.Values)
        {
            try { await service.StopAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var sessionId in _sessions.Keys)
        {
            try { await StopAllAsync(sessionId, CancellationToken.None); } catch { }
        }
        _sessions.Clear();
    }

    private static void ReapStaleServices()
    {
        if (!Directory.Exists(RegistryRoot))
            return;

        IEnumerable<string> records;
        try { records = Directory.EnumerateFiles(RegistryRoot, "*.json").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var record in records)
        {
            var removeRecord = false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(record));
                var root = document.RootElement;
                if (!root.TryGetProperty("processId", out var pidValue) || !pidValue.TryGetInt32(out var pid)
                    || !root.TryGetProperty("startTimeUtcTicks", out var startValue) || !startValue.TryGetInt64(out var startTicks))
                    continue;

                // Another daemon instance may still own a live managed service. Only reap
                // services whose recorded owner process has exited or whose PID was reused.
                if (root.TryGetProperty("ownerProcessId", out var ownerPidValue)
                    && ownerPidValue.TryGetInt32(out var ownerPid)
                    && root.TryGetProperty("ownerStartTimeUtcTicks", out var ownerStartValue)
                    && ownerStartValue.TryGetInt64(out var ownerStartTicks))
                {
                    try
                    {
                        using var owner = Process.GetProcessById(ownerPid);
                        if (Math.Abs(owner.StartTime.ToUniversalTime().Ticks - ownerStartTicks) <= TimeSpan.FromSeconds(2).Ticks)
                            continue;
                    }
                    catch (ArgumentException)
                    {
                        // The daemon that registered this service has exited.
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Cannot safely determine whether another daemon still owns it.
                        continue;
                    }
                }

                try
                {
                    using var process = Process.GetProcessById(pid);
                    var actualTicks = process.StartTime.ToUniversalTime().Ticks;
                    if (Math.Abs(actualTicks - startTicks) > TimeSpan.FromSeconds(2).Ticks)
                    {
                        // PID has been reused; never kill an unrelated process.
                        removeRecord = true;
                    }
                    else
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                        try { process.WaitForExit(3000); } catch (InvalidOperationException) { }
                        removeRecord = process.HasExited;
                    }
                }
                catch (ArgumentException)
                {
                    // Process is already gone.
                    removeRecord = true;
                }
                catch (InvalidOperationException)
                {
                    // Process information became unavailable during reaping.
                    removeRecord = false;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Keep the registry entry so a later daemon run can retry with available rights.
                    removeRecord = false;
                }
            }
            catch (JsonException)
            {
                // Do not guess a PID from a malformed registry record.
                removeRecord = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                removeRecord = false;
            }
            finally
            {
                if (removeRecord)
                {
                    try { File.Delete(record); } catch { }
                }
            }
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, ct);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            return false;
        }
    }

    private static int? TryExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return null; }
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; } catch { return null; }
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, IReadOnlyList<string> required)
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = JsonSerializer.SerializeToNode(required),
                    ["additionalProperties"] = false
                }
            }
        };
    }

    private static JsonObject ArgvSchema() => new()
    {
        ["type"] = "array",
        ["minItems"] = 1,
        ["maxItems"] = 128,
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = "Executable followed by its arguments; no shell interpretation."
    };

    private static string? ReadString(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static int? ReadInt(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    private sealed class ManagedService(
        string id,
        string sessionId,
        string name,
        string logPath,
        string registryPath,
        Process process,
        int? port,
        Regex readyPattern)
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private string _status = "starting";
        private DateTimeOffset? _stoppedAt;
        private int? _exitCode;
        private bool _logTruncated;

        public string Id { get; } = id;
        public DateTimeOffset StartedAt => _startedAt;
        public long DurationMs => (long)((_stoppedAt ?? DateTimeOffset.UtcNow) - _startedAt).TotalMilliseconds;

        public bool PersistRegistry()
        {
            try
            {
                using var owner = Process.GetCurrentProcess();
                var metadata = JsonSerializer.Serialize(new
                {
                    processId = process.Id,
                    startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    ownerProcessId = owner.Id,
                    ownerStartTimeUtcTicks = owner.StartTime.ToUniversalTime().Ticks,
                    serviceId = id,
                    sessionId
                });
                var temp = registryPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temp, metadata, new UTF8Encoding(false));
                    File.Move(temp, registryPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch { }
                    throw;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
        public string OutputTail { get { lock (_sync) return _output.ToString(); } }

        public void Append(string stream, string? line)
        {
            if (line is null)
                return;
            var formatted = $"[{stream}] {line}";
            lock (_sync)
            {
                if (_output.Length > 0)
                    _output.AppendLine();
                _output.Append(formatted);
                if (_output.Length > 6000)
                    _output.Remove(0, _output.Length - 6000);

                try
                {
                    var bytes = Encoding.UTF8.GetByteCount(formatted) + Environment.NewLine.Length;
                    var currentLength = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                    if (currentLength + bytes <= MaxLogBytes)
                        File.AppendAllText(logPath, formatted + Environment.NewLine, new UTF8Encoding(false));
                    else
                        _logTruncated = true;
                }
                catch (IOException)
                {
                    _logTruncated = true;
                }
                catch (UnauthorizedAccessException)
                {
                    _logTruncated = true;
                }
            }
        }

        public bool MatchesReadyPattern()
        {
            var tail = OutputTail;
            try { return readyPattern.IsMatch(tail); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public void MarkReady()
        {
            lock (_sync)
            {
                if (_status == "starting")
                    _status = "ready";
            }
        }

        public void MarkExited()
        {
            lock (_sync)
            {
                if (_status is "stopped" or "failed")
                    return;
                try { _exitCode = process.ExitCode; } catch { }
                _stoppedAt = DateTimeOffset.UtcNow;
                _status = _exitCode == 0 ? "exited" : "failed";
            }
        }

        public void RefreshExitState()
        {
            try
            {
                if (process.HasExited)
                    MarkExited();
            }
            catch (InvalidOperationException) { }
        }

        public async Task StopAsync(CancellationToken ct)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                lock (_sync)
                {
                    if (_status is not "failed" and not "exited")
                        _status = "stopped";
                    _stoppedAt ??= DateTimeOffset.UtcNow;
                    try { _exitCode = process.HasExited ? process.ExitCode : _exitCode; } catch { }
                }
                try { process.Dispose(); } catch { }
                try { File.Delete(registryPath); } catch { }
            }
        }

        public object ToSnapshot()
        {
            RefreshExitState();
            lock (_sync)
            {
                return new
                {
                    serviceId = Id,
                    name,
                    status = _status,
                    processId = SafeProcessId(process),
                    port,
                    exitCode = _exitCode,
                    startedAt = _startedAt,
                    durationMs = DurationMs,
                    logId = Id,
                    logTruncated = _logTruncated,
                    outputTail = _output.ToString()
                };
            }
        }
    }
}
