namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Executes an explicitly allowlisted command directly, without a shell, in a fixed project
/// working directory. The allowlist is workstation-local and empty by default.
/// </summary>
internal static class NativeCommandRunner
{
    private const int MaxOutputCharacters = 12_000;
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 180;

    private static readonly string[] SafeEnvironmentNames =
    [
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "TEMP", "TMP", "TMPDIR", "SystemRoot", "WINDIR",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"
    ];

    public static JsonObject ToolDefinition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "run_command",
            ["description"] = "Run an allowlisted command directly (no shell) in the current project directory. The workstation owner must configure AllowedCommandPrefixes; each call has a timeout and output limit.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["executable"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Executable name only, such as dotnet or npm; executable paths are not accepted."
                    },
                    ["arguments"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Argument vector passed directly to the process, not interpreted by a shell."
                    },
                    ["timeoutSeconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = MaxTimeoutSeconds,
                        ["description"] = "Optional timeout, default 60 seconds."
                    }
                },
                ["required"] = new JsonArray { JsonValue.Create("executable"), JsonValue.Create("arguments") },
                ["additionalProperties"] = false
            }
        }
    };

    public static async Task<McpToolResult> ExecuteAsync(
        string workingDirectory,
        JsonObject arguments,
        IReadOnlyCollection<string> allowedPrefixes,
        CancellationToken ct)
    {
        if (!Directory.Exists(workingDirectory))
            return new McpToolResult(true, "Command rejected: the project working directory does not exist.");

        var executable = ReadString(arguments, "executable")?.Trim();
        if (string.IsNullOrWhiteSpace(executable))
            return new McpToolResult(true, "Command rejected: 'executable' is required.");
        if (Path.IsPathRooted(executable)
            || executable.Contains('/') || executable.Contains('\\')
            || executable.Contains(':') || executable.Contains('\0'))
            return new McpToolResult(true, "Command rejected: executable must be a name from PATH, not a path.");

        if (arguments["arguments"] is not JsonArray argumentArray
            || argumentArray.Any(x => x is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.String))
            return new McpToolResult(true, "Command rejected: 'arguments' must be an array of strings.");

        var argv = argumentArray.Select(x => x!.GetValue<string>()).ToArray();
        if (argv.Any(IsUnsafePathArgument))
            return new McpToolResult(true, "Command rejected: absolute paths and parent-directory traversal are not allowed in arguments.");

        if (!MatchesAllowlist(executable, argv, allowedPrefixes))
            return new McpToolResult(true, "Command rejected: it does not match any configured AllowedCommandPrefixes entry.");

        var timeoutSeconds = arguments["timeoutSeconds"] is JsonValue timeoutValue
            && timeoutValue.TryGetValue<int>(out var configuredTimeout)
            ? configuredTimeout
            : DefaultTimeoutSeconds;
        if (timeoutSeconds < 1 || timeoutSeconds > MaxTimeoutSeconds)
            return new McpToolResult(true, $"Command rejected: timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in argv)
            startInfo.ArgumentList.Add(arg);

        // Do not forward arbitrary user/application environment variables to build scripts.
        var inherited = SafeEnvironmentNames
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null)
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var pair in inherited)
            startInfo.Environment[pair.Name] = pair.Value!;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new McpToolResult(true, "Command failed: the process did not start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new McpToolResult(true, $"Command failed to start: {ex.GetType().Name}.");
        }

        // No interactive stdin. A command that requires user input should fail instead of hanging.
        process.StandardInput.Close();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputCharacters);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            var stdoutAfterTimeout = await stdoutTask;
            var stderrAfterTimeout = await stderrTask;
            ct.ThrowIfCancellationRequested();
            return new McpToolResult(true, FormatResult(null, true, stdoutAfterTimeout, stderrAfterTimeout));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new McpToolResult(false, FormatResult(process.ExitCode, false, stdout, stderr));
    }

    private static bool MatchesAllowlist(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> allowedPrefixes)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in allowedPrefixes)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            // Settings use simple, unquoted token prefixes, e.g. "dotnet test" or "npm run test".
            var prefix = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (prefix.Length == 0)
                continue;
            if (!string.Equals(Path.GetFileName(prefix[0]), Path.GetFileName(executable), comparison))
                continue;
            if (arguments.Count < prefix.Length - 1)
                continue;

            var match = true;
            for (var i = 1; i < prefix.Length; i++)
            {
                if (!string.Equals(arguments[i - 1], prefix[i], comparison))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static bool IsUnsafePathArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Contains('\0'))
            return true;
        if (Path.IsPathRooted(argument)
            || Regex.IsMatch(argument, @"^[A-Za-z]:[\\/]")
            || argument.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // Arguments may embed paths in options (for example --project=../outside.csproj).
        // Check path boundaries inside the whole argument, not only when the argument is a path.
        if (Regex.IsMatch(argument, @"(^|[=:])([A-Za-z]:[\\/]|/|\\\\)"))
            return true;

        var normalized = argument.Replace('\\', '/');
        return Regex.IsMatch(normalized, @"(^|[=:/])\.\.(?:/|$)");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var output = new StringBuilder(Math.Min(1024, maxCharacters));
        var truncated = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (output.Length < maxCharacters)
            {
                if (output.Length > 0)
                    output.AppendLine();
                var remaining = maxCharacters - output.Length;
                output.Append(line.AsSpan(0, Math.Min(remaining, line.Length)));
                if (line.Length > remaining)
                    truncated = true;
            }
            else
            {
                truncated = true;
            }
        }

        if (truncated)
            output.AppendLine().Append("[output truncated by ProjectBeacon]");
        return output.ToString();
    }

    private static string FormatResult(int? exitCode, bool timedOut, string stdout, string stderr)
    {
        var result = new StringBuilder();
        result.AppendLine(timedOut ? "Command timed out and was terminated." : $"Exit code: {exitCode}.");
        if (!string.IsNullOrWhiteSpace(stdout))
            result.AppendLine("STDOUT:").AppendLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            result.AppendLine("STDERR:").AppendLine(stderr);
        return result.ToString().TrimEnd();
    }

    private static string? ReadString(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
