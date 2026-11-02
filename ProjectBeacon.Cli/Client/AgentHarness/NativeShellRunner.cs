namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Runs an interactive-style shell command on the user's workstation, scoped to the selected
/// project as its initial working directory. The native runtime only advertises this tool for
/// direct chat; pipeline Actors need an explicit subtask grant and Plan mode blocks it.
/// </summary>
internal static class NativeShellRunner
{
    private const int MaxOutputCharacters = 16_000;
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 300;
    private const int MaxCommandCharacters = 32_000;

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
            ["name"] = "run_shell",
            ["description"] = "Run a shell command on the user's workstation, starting in the selected project directory. Supports normal command chaining, pipes, git and GitHub CLI. The command runs as the workstation user and can access files and tools that user can access; do not use it for unrelated system changes. Commands are non-interactive, timed and output-limited.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Shell command to execute, e.g. git status --short, dotnet test, or gh auth status."
                    },
                    ["timeoutSeconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = MaxTimeoutSeconds,
                        ["description"] = "Optional timeout in seconds. Default 120, maximum 300."
                    }
                },
                ["required"] = new JsonArray { JsonValue.Create("command") },
                ["additionalProperties"] = false
            }
        }
    };

    public static async Task<McpToolResult> ExecuteAsync(
        string workingDirectory,
        JsonObject arguments,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new McpToolResult(true, "Shell command rejected: the project working directory does not exist.");

        var command = ReadString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new McpToolResult(true, "Shell command rejected: 'command' is required.");
        if (command.Length > MaxCommandCharacters)
            return new McpToolResult(true, $"Shell command rejected: maximum length is {MaxCommandCharacters} characters.");

        var timeoutSeconds = arguments["timeoutSeconds"] is JsonValue timeoutValue
            && timeoutValue.TryGetValue<int>(out var configuredTimeout)
                ? configuredTimeout
                : DefaultTimeoutSeconds;
        if (timeoutSeconds < 1 || timeoutSeconds > MaxTimeoutSeconds)
            return new McpToolResult(true, $"Shell command rejected: timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC");
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
                startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        // Keep normal user PATH/home so installed Git and gh authentication work, but do not
        // pass application secrets or arbitrary daemon environment variables to child processes.
        var inherited = SafeEnvironmentNames
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null)
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var pair in inherited)
            startInfo.Environment[pair.Name] = pair.Value!;
        startInfo.Environment["CI"] = "true";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new McpToolResult(true, "Shell command failed to start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new McpToolResult(true, $"Shell command failed to start: {ex.GetType().Name}.");
        }

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

    private static string? ReadString(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
