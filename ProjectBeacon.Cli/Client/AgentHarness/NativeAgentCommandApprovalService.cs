namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Owns user-interaction flows required by command execution: collecting command-prefix approvals
/// and pausing/resuming the owning model-task lease for individual command consent.
/// </summary>
internal sealed class NativeAgentCommandApprovalService(Action<string>? log)
{
    private readonly Action<string>? _log = log;

    internal static bool TryReadCommandPrefixSuggestions(JsonObject arguments, out string[] suggestions)
    {
        suggestions = [];
        if (arguments["additionalPrefixes"] is null)
            return true;
        if (arguments["additionalPrefixes"] is not JsonArray array
            || array.Count > 20
            || array.Any(item => item is not JsonValue value || value.GetValueKind() != JsonValueKind.String))
            return false;
        suggestions = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

    public async Task<McpToolResult> RequestCommandSuggestionsAsync(
        NativeAgentSessionState session,
        string projectRoot,
        string workingDirectory,
        IReadOnlyList<(string Prefix, string Source)> suggestions,
        IModelTaskLease taskLease,
        CancellationToken ct)
    {
        await session.CommandPermissionGate.WaitAsync(ct);
        try
        {
            var formId = "command-suggestions:" + Guid.NewGuid().ToString("N");
            var allSuggestions = suggestions.ToArray();
            var safeSuggestions = allSuggestions.Take(50).ToArray();
            var truncated = allSuggestions.Length > safeSuggestions.Length;
            var options = new JsonArray();
            var defaults = new JsonArray();
            foreach (var suggestion in safeSuggestions)
            {
                options.Add(new JsonObject
                {
                    ["value"] = suggestion.Prefix,
                    ["label"] = suggestion.Prefix + " — " + suggestion.Source
                });
                defaults.Add(JsonValue.Create(suggestion.Prefix));
            }

            var fields = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "prefixes",
                    ["title"] = "Command prefixes to approve",
                    ["description"] = "These are argv prefixes, not shell commands. They were read from project manifests, CI workflow run steps, or proposed by the agent. Uncheck anything you do not want this project to run. Selected entries are stored locally for this project only.",
                    ["type"] = "multiselect",
                    ["options"] = options,
                    ["default"] = defaults
                }
            };
            var form = new AgentRuntimeForm(
                formId,
                "Review project command suggestions",
                "No command has been executed. Review the exact prefixes and submit your selection to approve them.",
                fields.ToJsonString());
            var pending = new NativePendingFormState(form,
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!session.PendingForms.TryAdd(formId, pending))
                return new McpToolResult(true, "Could not register the command suggestion form.");

            try
            {
                var answerJson = await taskLease.WaitForUserAsync(pending.Completion.Task, ct, pending.RegisterResumeCallback);
                if (!TryReadApprovedCommandPrefixes(answerJson, out var selected))
                    return new McpToolResult(true, "The command suggestion answer was malformed; no prefixes were approved.");

                var allowed = safeSuggestions.Select(item => item.Prefix).ToHashSet(StringComparer.OrdinalIgnoreCase);
                selected = selected.Where(allowed.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var approved = 0;
                foreach (var prefix in selected)
                {
                    if (CommandExecutionPolicy.ApprovePrefix(
                        projectRoot,
                        prefix,
                        CommandExecutionPolicy.GetManifestFingerprint(workingDirectory),
                        out var error))
                        approved++;
                    else
                        _log?.Invoke("could not persist command prefix approval: " + (error ?? "unknown error"));
                }

                var selectedAll = !truncated
                    && safeSuggestions.Length > 0
                    && selected.Length == safeSuggestions.Length
                    && safeSuggestions.All(item => selected.Contains(item.Prefix, StringComparer.OrdinalIgnoreCase));
                var manifestSourcesMatch = string.Equals(
                    CommandExecutionPolicy.GetManifestFingerprint(projectRoot),
                    CommandExecutionPolicy.GetManifestFingerprint(workingDirectory),
                    StringComparison.Ordinal);
                var fingerprintTrusted = selectedAll
                    && manifestSourcesMatch
                    && CommandExecutionPolicy.TrustCurrentManifestFingerprint(projectRoot);

                return new McpToolResult(false, JsonSerializer.Serialize(new
                {
                    approvedCount = approved,
                    selectedPrefixes = selected,
                    manifestFingerprintTrusted = fingerprintTrusted,
                    truncated,
                    note = selected.Length == 0
                        ? "No command prefixes were approved."
                        : truncated
                            ? "Only the first 50 suggestions were displayed. Selected prefixes were approved locally, but the manifest fingerprint remains untrusted until the full list is reviewed."
                            : fingerprintTrusted
                                ? "All listed prefixes were approved. The current manifest and workflow fingerprint is now trusted for future turns."
                                : "Selected prefixes were approved locally. Other manifest-derived prefixes will still require confirmation."
                }));
            }
            finally
            {
                session.PendingForms.TryRemove(formId, out _);
            }
        }
        finally
        {
            session.CommandPermissionGate.Release();
        }
    }

    private static bool TryReadApprovedCommandPrefixes(string? answerJson, out string[] prefixes)
    {
        prefixes = [];
        if (string.IsNullOrWhiteSpace(answerJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(answerJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("prefixes", out var array)
                || array.ValueKind != JsonValueKind.Array
                || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                return false;
            prefixes = array.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<(bool Allowed, string? Error)> RequestCommandPermissionAsync(
        NativeAgentSessionState session,
        string workingDirectory,
        string approvalProjectRoot,
        IReadOnlyList<string> argv,
        IModelTaskLease taskLease,
        CancellationToken ct)
    {
        await session.CommandPermissionGate.WaitAsync(ct);
        try
        {
            var formId = "command-permission:" + Guid.NewGuid().ToString("N");
            var prefix = CommandExecutionPolicy.CreateApprovalPrefix(argv);
            var form = CreateCommandPermissionForm(formId, workingDirectory, argv, prefix);
            var pending = new NativePendingFormState(form,
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!session.PendingForms.TryAdd(formId, pending))
                throw new InvalidOperationException("Could not register the command permission request.");
            try
            {
                var answerJson = await taskLease.WaitForUserAsync(pending.Completion.Task, ct, pending.RegisterResumeCallback);
                switch (ParseCommandPermissionDecision(answerJson))
                {
                    case "once":
                        return (true, null);
                    case "always":
                        if (CommandExecutionPolicy.ApprovePrefix(
                            approvalProjectRoot,
                            prefix,
                            CommandExecutionPolicy.GetManifestFingerprint(workingDirectory),
                            out var error))
                            return (true, null);
                        var message = "Could not save the project command approval: " + (error ?? "unknown error");
                        _log?.Invoke("could not persist project command approval: " + (error ?? "unknown error"));
                        return (false, message);
                    default:
                        return (false, null);
                }
            }
            finally
            {
                session.PendingForms.TryRemove(formId, out _);
            }
        }
        finally
        {
            session.CommandPermissionGate.Release();
        }
    }

    internal static AgentRuntimeForm CreateCommandPermissionForm(
        string formId,
        string workingDirectory,
        IReadOnlyList<string> argv,
        string prefix)
    {
        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? "(unknown)"
            : Path.GetFullPath(workingDirectory);
        var commandDetails = JsonSerializer.Serialize(argv);
        var description =
            $"Command (argv):{Environment.NewLine}{commandDetails}{Environment.NewLine}{Environment.NewLine}" +
            $"Approval prefix saved for this project: {prefix}{Environment.NewLine}{Environment.NewLine}" +
            $"Working directory: {directory}{Environment.NewLine}{Environment.NewLine}" +
            "The process runs without shell interpretation, with a temporary HOME and without Beacon tokens or provider keys. " +
            "It can still execute repository scripts and may access the network or write outside the worktree; a worktree is not an OS security boundary.";
        var fields = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "decision",
                ["title"] = "Permission",
                ["description"] = description,
                ["type"] = "string",
                ["options"] = new JsonArray
                {
                    new JsonObject { ["value"] = "once", ["label"] = "Allow this command once" },
                    new JsonObject { ["value"] = "always", ["label"] = "Always allow this prefix for this project" },
                    new JsonObject { ["value"] = "deny", ["label"] = "Deny" }
                }
            }
        };
        return new AgentRuntimeForm(formId, "Command approval requested",
            "The agent is waiting for your approval before running an unfamiliar command.",
            fields.ToJsonString());
    }

    internal static string ParseCommandPermissionDecision(string? answerJson)
    {
        if (string.IsNullOrWhiteSpace(answerJson))
            return "deny";
        try
        {
            using var document = JsonDocument.Parse(answerJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("decision", out var decision)
                || decision.ValueKind != JsonValueKind.String)
                return "deny";
            return decision.GetString()?.Trim().ToLowerInvariant() switch
            {
                "once" => "once",
                "always" => "always",
                _ => "deny"
            };
        }
        catch (JsonException)
        {
            return "deny";
        }
    }

    internal static bool TryReadCommandArgv(JsonObject arguments, out string[] argv)
    {
        argv = [];
        if (arguments["argv"] is not JsonArray array
            || array.Count == 0
            || array.Any(item => item is not JsonValue value || value.GetValueKind() != JsonValueKind.String))
            return false;
        argv = array.Select(item => item!.GetValue<string>()).ToArray();
        return true;
    }

}
