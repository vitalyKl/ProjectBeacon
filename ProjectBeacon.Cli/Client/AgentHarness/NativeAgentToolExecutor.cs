namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Executes one already-parsed tool call. It owns command authorization, approval flow,
/// scoped MCP dispatch, service operations, and the per-turn read-result cache.
/// </summary>
internal sealed class NativeAgentToolExecutor(
    Func<WorkstationSettings> loadSettings,
    NativeServiceManager services,
    NativeAgentCommandApprovalService commandApproval,
    Action<string>? log)
{
    private const int MaxToolResultCharacters = 16 * 1024;

    private readonly Func<WorkstationSettings> _loadSettings = loadSettings;
    private readonly NativeServiceManager _services = services;
    private readonly NativeAgentCommandApprovalService _commandApproval = commandApproval;
    private readonly Action<string>? _log = log;

    public async Task<NativeExecutedToolCall> ExecuteAsync(
        NativeToolExecutionContext context,
        NativeToolCallAccumulator call,
        CancellationToken ct)
    {
        var session = context.Session;
        var agentMode = context.AgentMode;
        var controls = context.Controls;
        var mcpClient = context.McpClient;
        var mcpTools = context.AdvertisedTools;
        var repeatedToolCall = context.RepeatedToolCall;
        var toolResultCache = context.ResultCache;
        var taskLease = context.TaskLease;
        var manifestFingerprint = context.ManifestFingerprint;
        var manifestTrustedAtTurnStart = context.ManifestTrustedAtTurnStart;


        ct.ThrowIfCancellationRequested();

        var startedAt = Stopwatch.GetTimestamp();
        var isError = false;
        var hasArguments = call.TryReadArguments(out var parsedArguments);
        var cacheKey = hasArguments && NativeAgentToolPolicy.IsCacheableTool(call.Name)
            ? NativeAgentToolPolicy.CreateToolCacheKey(call.Name!, parsedArguments!)
            : null;
        var cacheHit = false;
        string toolResult;
        try
        {
            if (cacheKey is not null && toolResultCache.TryGetValue(cacheKey, out var cachedResult))
            {
                _log?.Invoke($"native tool cache hit: {call.Name}");
                cacheHit = true;
                isError = cachedResult.IsError;
                toolResult = "[Cached result from an earlier identical read-only call]\n" + cachedResult.Text;
            }
            else
            {
                if (cacheKey is not null)
                    _log?.Invoke($"native tool cache miss: {call.Name}");
                if (NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked(agentMode, call.Name))
                {
                    isError = true;
                    toolResult = "Tool error: command and service operations are disabled in Plan mode. Use read-only inspection tools and return a plan.";
                }
                else if (repeatedToolCall)
                {
                    isError = true;
                    toolResult = "Tool error: this exact tool call with identical arguments was already repeated. Do not repeat it again; choose a different action or provide a final answer.";
                }
                else if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
                {
                    isError = true;
                    toolResult = "Tool error: the model returned a tool call without a valid id or name.";
                }
                else if (!hasArguments)
                {
                    isError = true;
                    toolResult = "Tool error: the model returned malformed JSON arguments. Return a valid JSON object and try again.";
                }
                else if (string.Equals(call.Name, "repo_status", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true, PipelineRole: "Actor" or "Planner" } repoAuthorization)
                    {
            isError = true;
            toolResult = "Tool error: repo_status is available only to native pipeline Actor and Planner sessions.";
                    }
                    else
                    {
            toolResult = await ProjectBeacon.Cli.Client.GitSyncService.ReadStatusForAgentAsync(
                repoAuthorization.ProjectRoot ?? repoAuthorization.WorkingDirectory, ct);
                    }
                }
                else if (string.Equals(call.Name, "suggest_command_prefixes", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true } suggestionAuthorization)
                    {
            isError = true;
            toolResult = "Tool error: command suggestions require an isolated project workspace.";
                    }
                    else if (!NativeAgentCommandApprovalService.TryReadCommandPrefixSuggestions(parsedArguments!, out var agentSuggestions))
                    {
            isError = true;
            toolResult = "Tool error: additionalPrefixes must be an array of strings.";
                    }
                    else
                    {
            var projectRoot = suggestionAuthorization.ProjectRoot ?? suggestionAuthorization.WorkingDirectory;
            var suggestions = CommandExecutionPolicy.GetCommandSuggestions(
                suggestionAuthorization.WorkingDirectory,
                agentSuggestions);
            if (suggestions.Count == 0)
            {
                toolResult = "No safe command prefixes were detected in the project manifests or CI workflows. Use run_command to request approval for a specific argv command.";
            }
            else
            {
                var result = await _commandApproval.RequestCommandSuggestionsAsync(
                    session,
                    projectRoot,
                    suggestionAuthorization.WorkingDirectory,
                    suggestions,
                    taskLease,
                    ct);
                isError = result.IsError;
                toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
            }
                    }
                }
                else if (string.Equals(call.Name, "run_command", StringComparison.Ordinal))
                {
                    var policy = CommandExecutionPolicy.NormalizeMode(_loadSettings().CommandPolicy);
                    if (policy == CommandExecutionPolicy.Off
            || controls?.HarnessAuthorization is not { IsolatedWorkspace: true } commandAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(commandAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: run_command is disabled by the workstation policy or execution role.";
                    }
                    else if (!NativeAgentCommandApprovalService.TryReadCommandArgv(parsedArguments!, out var commandArgv))
                    {
            isError = true;
            toolResult = "Tool error: run_command requires argv as a non-empty array of strings.";
                    }
                    else if (!CommandExecutionPolicy.TryValidate(commandArgv, commandAuthorization.WorkingDirectory, out var commandError))
                    {
            isError = true;
            toolResult = "Tool error: " + commandError;
                    }
                    else
                    {
            var approvalProjectRoot = commandAuthorization.ProjectRoot ?? commandAuthorization.WorkingDirectory;
            var executionManifestFingerprint = CommandExecutionPolicy.GetManifestFingerprint(commandAuthorization.WorkingDirectory);
            var manuallyApproved = CommandExecutionPolicy.GetManuallyApprovedPrefixes(
                approvalProjectRoot, executionManifestFingerprint);
            var manifestUnchanged = string.Equals(
                manifestFingerprint,
                executionManifestFingerprint,
                StringComparison.Ordinal);
            var manifestPrefixes = manifestTrustedAtTurnStart && manifestUnchanged
                ? CommandExecutionPolicy.DetectManifestPrefixes(commandAuthorization.WorkingDirectory)
                : Array.Empty<string>();
            var isApproved = CommandExecutionPolicy.MatchesApprovedPrefix(commandArgv, manuallyApproved)
                || CommandExecutionPolicy.MatchesApprovedPrefix(commandArgv, manifestPrefixes);
            (bool Allowed, string? Error) permission = (true, null);
            if (policy == CommandExecutionPolicy.Ask && !isApproved)
                permission = await _commandApproval.RequestCommandPermissionAsync(
                    session,
                    commandAuthorization.WorkingDirectory,
                    approvalProjectRoot,
                    commandArgv,
                    taskLease,
                    ct);

            if (!permission.Allowed)
            {
                isError = true;
                toolResult = permission.Error is null
                    ? "Tool error: the user denied this command. Do not retry it or change its arguments to bypass the decision."
                    : "Tool error: " + permission.Error;
            }
            else
            {
                var result = await NativeCommandRunner.ExecuteAsync(
                    commandAuthorization.WorkingDirectory,
                    session.Id,
                    parsedArguments!,
                    ct,
                    commandAuthorization.ProjectId.ToString("N"));
                isError = result.IsError;
                toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
            }
                    }
                }
                else if (string.Equals(call.Name, "run_log", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true } logAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(logAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: run_log is not authorized for this execution role.";
                    }
                    else
                    {
            var result = await NativeCommandRunner.ReadLogAsync(session.Id, parsedArguments!, ct);
            isError = result.IsError;
            toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
                    }
                }
                else if (string.Equals(call.Name, "service_start", StringComparison.Ordinal))
                {
                    var policy = CommandExecutionPolicy.NormalizeMode(_loadSettings().CommandPolicy);
                    if (policy == CommandExecutionPolicy.Off
            || controls?.HarnessAuthorization is not { IsolatedWorkspace: true } serviceAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(serviceAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: service_start is disabled by the workstation policy or execution role.";
                    }
                    else if (!NativeAgentCommandApprovalService.TryReadCommandArgv(parsedArguments!, out var serviceArgv))
                    {
            isError = true;
            toolResult = "Tool error: service_start requires argv as a non-empty array of strings.";
                    }
                    else if (!CommandExecutionPolicy.TryValidate(serviceArgv, serviceAuthorization.WorkingDirectory, out var serviceCommandError))
                    {
            isError = true;
            toolResult = "Tool error: " + serviceCommandError;
                    }
                    else
                    {
            var approvalProjectRoot = serviceAuthorization.ProjectRoot ?? serviceAuthorization.WorkingDirectory;
            var executionManifestFingerprint = CommandExecutionPolicy.GetManifestFingerprint(serviceAuthorization.WorkingDirectory);
            var manuallyApproved = CommandExecutionPolicy.GetManuallyApprovedPrefixes(
                approvalProjectRoot, executionManifestFingerprint);
            var manifestUnchanged = string.Equals(
                manifestFingerprint,
                executionManifestFingerprint,
                StringComparison.Ordinal);
            var manifestPrefixes = manifestTrustedAtTurnStart && manifestUnchanged
                ? CommandExecutionPolicy.DetectManifestPrefixes(serviceAuthorization.WorkingDirectory)
                : Array.Empty<string>();
            var isApproved = CommandExecutionPolicy.MatchesApprovedPrefix(serviceArgv, manuallyApproved)
                || CommandExecutionPolicy.MatchesApprovedPrefix(serviceArgv, manifestPrefixes);
            (bool Allowed, string? Error) permission = (true, null);
            if (policy == CommandExecutionPolicy.Ask && !isApproved)
                permission = await _commandApproval.RequestCommandPermissionAsync(
                    session,
                    serviceAuthorization.WorkingDirectory,
                    approvalProjectRoot,
                    serviceArgv,
                    taskLease,
                    ct);

            if (!permission.Allowed)
            {
                isError = true;
                toolResult = permission.Error is null
                    ? "Tool error: the user denied this service command. Do not retry it or change its arguments to bypass the decision."
                    : "Tool error: " + permission.Error;
            }
            else
            {
                var result = await _services.StartAsync(
                    session.Id,
                    serviceAuthorization.WorkingDirectory,
                    parsedArguments!,
                    ct);
                isError = result.IsError;
                toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
            }
                    }
                }
                else if (string.Equals(call.Name, "service_status", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true } serviceAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(serviceAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: service_status is not authorized for this execution role.";
                    }
                    else
                    {
            var result = _services.Status(session.Id, parsedArguments!);
            isError = result.IsError;
            toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
                    }
                }
                else if (string.Equals(call.Name, "service_logs", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true } serviceAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(serviceAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: service_logs is not authorized for this execution role.";
                    }
                    else
                    {
            var result = await _services.LogsAsync(session.Id, parsedArguments!, ct);
            isError = result.IsError;
            toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
                    }
                }
                else if (string.Equals(call.Name, "service_stop", StringComparison.Ordinal))
                {
                    if (controls?.HarnessAuthorization is not { IsolatedWorkspace: true } serviceAuthorization
            || !NativeAgentToolPolicy.IsRunCommandAuthorized(serviceAuthorization))
                    {
            isError = true;
            toolResult = "Tool error: service_stop is not authorized for this execution role.";
                    }
                    else
                    {
            var result = await _services.StopAsync(session.Id, parsedArguments!, ct);
            isError = result.IsError;
            toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
                    }
                }
                else if (mcpClient is null)
                {
                    isError = true;
                    toolResult = "Tool error: no scoped MCP authorization is available for this command.";
                }
                else if (!NativeAgentToolPolicy.IsAdvertisedMcpTool(mcpTools, call.Name!))
                {
                    isError = true;
                    toolResult = $"Tool error: MCP tool '{call.Name}' was not advertised by the scoped server and cannot be executed.";
                }
                else
                {
                    var result = await mcpClient.CallToolAsync(call.Name!, parsedArguments!, ct);
                    isError = result.IsError;
                    toolResult = result.IsError
            ? "Tool error: " + result.Text
            : result.Text;
                }
                if (cacheKey is not null && !isError)
                    toolResultCache.TryAdd(cacheKey, new McpToolResult(false, toolResult));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            isError = true;
            toolResult = "Tool error: " + SafeError(ex);
        }
        var durationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return new NativeExecutedToolCall(call, TruncateToolResult(toolResult), isError, durationMs, cacheHit);
                
    }

    private static string TruncateToolResult(string result)
    {
        if (result.Length <= MaxToolResultCharacters)
            return result;
        var end = MaxToolResultCharacters;
        if (end > 0 && char.IsHighSurrogate(result[end - 1]))
            end--;
        return result[..end] + Environment.NewLine + "[Tool result truncated by the native runtime.]";
    }

    private static string SafeError(Exception ex) => ex switch
    {
        ChatProviderException provider => provider.Message,
        HttpRequestException => "The chat provider connection failed.",
        OperationCanceledException => "The generation was cancelled.",
        InvalidDataException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => "The native agent runtime encountered an unexpected error."
    };
}
