namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class NativeCommandRunner
{
    private const int MaxOutputCharacters = 12_000;
    private const int MaxLogBytes = 16 * 1024 * 1024;
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 180;
    private const int DefaultLogPageSize = 100;
    private const int MaxLogPageSize = 250;

    private static readonly string[] InheritedEnvironmentNames =
    [
        "PATH", "SystemRoot", "WINDIR", "COMSPEC",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "LANG", "LC_ALL",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER"
    ];

    public static JsonObject ToolDefinition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "run_command",
            ["description"] = "Run one executable with an argv array (no shell) in the current isolated project worktree. Returns a structured exit code, duration, bounded output tail, detected error lines, and a runId for reading the full log with run_log. Commands may be blocked by the workstation policy.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["argv"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = 128,
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Executable followed by its arguments, e.g. [\"dotnet\", \"test\"] or [\"npm\", \"test\"]. No shell parsing or chaining."
                    },
                    ["timeoutSeconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = MaxTimeoutSeconds,
                        ["description"] = "Optional timeout, default 60 seconds."
                    }
                },
                ["required"] = new JsonArray { JsonValue.Create("argv") },
                ["additionalProperties"] = false
            }
        }
    };

    public static JsonObject RunLogToolDefinition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "run_log",
            ["description"] = "Read a page of the complete output from an earlier run_command call using its runId. Use the returned nextOffset to continue.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["runId"] = new JsonObject { ["type"] = "string", ["description"] = "runId returned by run_command." },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Line offset, default 0." },
                    ["maxLines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxLogPageSize, ["description"] = "Lines to read, default 100." }
                },
                ["required"] = new JsonArray { JsonValue.Create("runId") },
                ["additionalProperties"] = false
            }
        }
    };

    public static async Task<McpToolResult> ExecuteAsync(
        string workingDirectory,
        string sessionId,
        JsonObject arguments,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new McpToolResult(true, "Command rejected: the project working directory does not exist.");

        if (arguments["argv"] is not JsonArray argvNode
            || argvNode.Count == 0
            || argvNode.Any(item => item is not JsonValue value || value.GetValueKind() != JsonValueKind.String))
            return new McpToolResult(true, "Command rejected: 'argv' must be a non-empty array of strings.");

        var argv = argvNode.Select(item => item!.GetValue<string>()).ToArray();
        if (!CommandExecutionPolicy.TryValidate(argv, workingDirectory, out var validationError))
            return new McpToolResult(true, "Command rejected: " + validationError);

        var timeoutSeconds = arguments["timeoutSeconds"] is JsonValue timeoutValue
            && timeoutValue.TryGetValue<int>(out var configuredTimeout)
                ? configuredTimeout
                : DefaultTimeoutSeconds;
        if (timeoutSeconds < 1 || timeoutSeconds > MaxTimeoutSeconds)
            return new McpToolResult(true, $"Command rejected: timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        var sessionHome = SessionHomePath(sessionId);
        var tempDirectory = Path.Combine(sessionHome, "tmp");
        var logDirectory = Path.Combine(sessionHome, "logs");
        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(logDirectory);
        var runId = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(logDirectory, runId + ".log");
        var startInfo = CreateStartInfo(workingDirectory, sessionId, argv);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (!process.Start())
                return new McpToolResult(true, "Command failed: the process did not start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new McpToolResult(true, $"Command failed to start: {ex.GetType().Name}.");
        }

        process.StandardInput.Close();
        var outputGate = new SemaphoreSlim(1, 1);
        try
        {
            await using var logStream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, useAsync: true);
            await using var logWriter = new StreamWriter(logStream, new UTF8Encoding(false), leaveOpen: true);
            var stdoutTask = ReadOutputAsync(process.StandardOutput, "OUT", logWriter, logStream, outputGate);
            var stderrTask = ReadOutputAsync(process.StandardError, "ERR", logWriter, logStream, outputGate);
            var timedOut = false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                if (ct.IsCancellationRequested)
                {
                    try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                    await logWriter.FlushAsync(CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await logWriter.FlushAsync(ct);
            var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var outputTail = JoinOutputTails(stdout, stderr);
            var errors = ExtractErrorLines(outputTail);

            var result = new JsonObject
            {
                ["runId"] = runId,
                ["exitCode"] = timedOut ? null : JsonValue.Create(process.ExitCode),
                ["timedOut"] = timedOut,
                ["durationMs"] = durationMs,
                ["outputTail"] = outputTail,
                ["errorLines"] = JsonSerializer.SerializeToNode(errors),
                ["logTruncated"] = logStream.Length >= MaxLogBytes
            };
            return new McpToolResult(false, result.ToJsonString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            outputGate.Dispose();
        }
    }

    public static async Task<McpToolResult> ReadLogAsync(string sessionId, JsonObject arguments, CancellationToken ct)
    {
        var runId = ReadString(arguments, "runId")?.Trim();
        if (string.IsNullOrWhiteSpace(runId) || !Regex.IsMatch(runId, @"^[a-fA-F0-9]{32}$"))
            return new McpToolResult(true, "run_log rejected: runId is invalid.");

        var offset = ReadInt(arguments, "offset") ?? 0;
        var maxLines = ReadInt(arguments, "maxLines") ?? DefaultLogPageSize;
        if (offset < 0 || maxLines < 1 || maxLines > MaxLogPageSize)
            return new McpToolResult(true, $"run_log rejected: offset must be non-negative and maxLines must be 1..{MaxLogPageSize}.");

        var path = Path.Combine(SessionHomePath(sessionId), "logs", runId + ".log");
        if (!File.Exists(path))
            return new McpToolResult(true, "run_log: no output log exists for this runId in the current session.");

        var pageWithLookahead = new List<string>(maxLines + 1);
        var lineNumber = 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (lineNumber >= offset && pageWithLookahead.Count <= maxLines)
                pageWithLookahead.Add(line);
            lineNumber++;
            if (pageWithLookahead.Count > maxLines)
                break;
        }

        var hasMore = pageWithLookahead.Count > maxLines;
        if (hasMore)
            pageWithLookahead.RemoveAt(pageWithLookahead.Count - 1);
        var result = new JsonObject
        {
            ["runId"] = runId,
            ["offset"] = offset,
            ["lines"] = JsonSerializer.SerializeToNode(pageWithLookahead),
            ["nextOffset"] = hasMore ? JsonValue.Create(offset + pageWithLookahead.Count) : null,
            ["endOfLog"] = !hasMore
        };
        return new McpToolResult(false, result.ToJsonString());
    }

    internal static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        string sessionId,
        IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            throw new ArgumentException("argv cannot be empty.", nameof(argv));

        var sessionHome = SessionHomePath(sessionId);
        var tempDirectory = Path.Combine(sessionHome, "tmp");
        var isolatedHome = Path.Combine(sessionHome, "home");
        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(isolatedHome);

        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var safePath = BuildSafePath(Environment.GetEnvironmentVariable("PATH"), fullWorkingDirectory);
        var executablePath = ResolveExecutable(argv[0], safePath, fullWorkingDirectory);
        var isBatchShim = OperatingSystem.IsWindows()
            && (Path.GetExtension(executablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(executablePath).Equals(".bat", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = fullWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (isBatchShim)
        {
            // npm/pnpm/yarn commonly install as .cmd files on Windows. CreateProcess cannot
            // launch those directly. Use cmd.exe only as a fixed batch-shim adapter, quote every
            // argv token, disable delayed expansion, and reject the characters that cmd expands.
            var cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (!File.Exists(cmdPath))
                throw new FileNotFoundException("Windows command interpreter was not found.", cmdPath);

            var command = QuoteBatchToken(executablePath)
                + string.Concat(argv.Skip(1).Select(argument => " " + QuoteBatchToken(argument)));
            startInfo.FileName = cmdPath;
            startInfo.Arguments = "/d /v:off /s /c \"" + command + "\"";
        }
        else
        {
            startInfo.FileName = executablePath;
            foreach (var argument in argv.Skip(1))
                startInfo.ArgumentList.Add(argument);
        }

        var inherited = InheritedEnvironmentNames
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null)
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var pair in inherited)
        {
            if (string.Equals(pair.Name, "PATH", StringComparison.OrdinalIgnoreCase))
                startInfo.Environment[pair.Name] = safePath;
            else
                startInfo.Environment[pair.Name] = pair.Value!;
        }
        startInfo.Environment["PATH"] = safePath;
        if (OperatingSystem.IsWindows())
        {
            var trustedCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            startInfo.Environment["COMSPEC"] = trustedCmd;
        }

        // Do not forward application credentials or arbitrary daemon environment variables.
        startInfo.Environment["HOME"] = isolatedHome;
        startInfo.Environment["USERPROFILE"] = isolatedHome;
        startInfo.Environment["APPDATA"] = Path.Combine(isolatedHome, "AppData", "Roaming");
        startInfo.Environment["LOCALAPPDATA"] = Path.Combine(isolatedHome, "AppData", "Local");
        startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(isolatedHome, ".config");
        startInfo.Environment["TMP"] = tempDirectory;
        startInfo.Environment["TEMP"] = tempDirectory;
        startInfo.Environment["TMPDIR"] = tempDirectory;
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(isolatedHome, ".dotnet");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(isolatedHome, ".nuget", "packages");
        startInfo.Environment["npm_config_cache"] = Path.Combine(isolatedHome, ".npm");
        startInfo.Environment["CARGO_HOME"] = Path.Combine(isolatedHome, ".cargo");
        startInfo.Environment["PIP_CACHE_DIR"] = Path.Combine(isolatedHome, ".cache", "pip");
        startInfo.Environment["GOCACHE"] = Path.Combine(isolatedHome, ".cache", "go-build");
        startInfo.Environment["GOMODCACHE"] = Path.Combine(isolatedHome, "go", "pkg", "mod");
        startInfo.Environment["CI"] = "true";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
        return startInfo;
    }

    private static string BuildSafePath(string? path, string workingDirectory)
    {
        var workspaceRoot = FindWorkspaceRoot(workingDirectory);
        var safeEntries = new List<string>();
        foreach (var entry in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = entry.Trim().Trim('"');
            if (candidate.Length == 0)
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate, workingDirectory);
                var resolved = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                    fullPath = Path.GetFullPath(resolved.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (IsInsideDirectory(fullPath, workspaceRoot) || !Directory.Exists(fullPath))
                continue;
            if (!safeEntries.Contains(fullPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                safeEntries.Add(fullPath);
        }

        if (safeEntries.Count == 0)
            throw new InvalidOperationException("No executable search path remains after excluding the isolated project worktree.");
        return string.Join(Path.PathSeparator, safeEntries);
    }

    private static string ResolveExecutable(string executable, string safePath, string workingDirectory)
    {
        var extensions = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(executable))
            ? new[] { ".exe", ".com", ".cmd", ".bat" }
            : new[] { string.Empty };

        foreach (var directory in safePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var extension in extensions)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, executable + extension));
            if (!File.Exists(candidate))
                continue;

            var resolved = candidate;
            try
            {
                var target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    resolved = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (IsInsideDirectory(resolved, FindWorkspaceRoot(workingDirectory)))
                continue;
            return candidate;
        }

        throw new FileNotFoundException($"Executable '{executable}' was not found on the workstation PATH outside the project worktree.");
    }

    private static string FindWorkspaceRoot(string workingDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return current.FullName;
            current = current.Parent;
        }
        return Path.GetFullPath(workingDirectory);
    }

    private static bool IsInsideDirectory(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullCandidate, fullRoot, comparison)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string QuoteBatchToken(string value)
    {
        if (value.Contains('"') || value.Contains('%') || value.Contains('\r') || value.Contains('\n'))
            throw new InvalidOperationException("Windows .cmd/.bat commands cannot safely accept arguments containing quotes, percent signs, or newlines.");
        return "\"" + value + "\"";
    }

    internal static string RunLogPath(string sessionId, string runId)
        => Path.Combine(SessionHomePath(sessionId), "logs", runId + ".log");

    public static string SessionHomePath(string sessionId)
    {
        var safeId = new string((sessionId ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = Guid.NewGuid().ToString("N");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectBeacon",
            "command-sessions",
            safeId);
    }

    public static void CleanupSession(string sessionId)
    {
        var path = SessionHomePath(sessionId);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A locked service process must be stopped before session files can be deleted.
        }
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        string streamName,
        StreamWriter writer,
        FileStream logStream,
        SemaphoreSlim outputGate)
    {
        var tail = new StringBuilder();
        var truncated = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            var formatted = $"[{streamName}] {line}";
            await outputGate.WaitAsync();
            try
            {
                if (logStream.Length < MaxLogBytes)
                    await writer.WriteLineAsync(formatted);
                else
                    truncated = true;
            }
            finally
            {
                outputGate.Release();
            }

            if (tail.Length > 0)
                tail.AppendLine();
            tail.Append(formatted);
            if (tail.Length > MaxOutputCharacters)
            {
                tail.Remove(0, tail.Length - MaxOutputCharacters);
                truncated = true;
            }
        }

        if (truncated)
            tail.Insert(0, "[Earlier output omitted; use run_log for the saved log tail.]\n");
        return tail.ToString();
    }

    private static string JoinOutputTails(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;
        return stdout + Environment.NewLine + stderr;
    }

    private static IReadOnlyList<string> ExtractErrorLines(string output)
        => output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => Regex.IsMatch(line, @"\b(error|failed|failure|exception|fatal)\b", RegexOptions.IgnoreCase))
            .TakeLast(30)
            .ToArray();

    private static string? ReadString(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static int? ReadInt(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;
}
