namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Builds the exact tool catalog exposed for one native turn, applying task-role and agent-mode
/// policy before any provider request is made.
/// </summary>
internal sealed class NativeAgentToolCatalog(Func<WorkstationSettings> loadSettings)
{
    private readonly Func<WorkstationSettings> _loadSettings = loadSettings;

    public NativeAgentToolCatalogResult Build(
        string agentMode,
        AgentPromptControls? controls,
        IMcpToolClient? mcpClient)
    {
        var tools = new JsonArray();
        if (mcpClient is not null)
        {
            foreach (var tool in mcpClient.OpenAiTools)
                tools.Add(tool?.DeepClone());
        }

        var commandPolicy = CommandExecutionPolicy.NormalizeMode(_loadSettings().CommandPolicy);
        var manifestFingerprint = controls?.HarnessAuthorization is { IsolatedWorkspace: true } fingerprintAuthorization
            ? CommandExecutionPolicy.GetManifestFingerprint(fingerprintAuthorization.WorkingDirectory)
            : null;
        var manifestTrustedAtTurnStart = controls?.HarnessAuthorization is { IsolatedWorkspace: true } trustAuthorization
            && CommandExecutionPolicy.ManifestSourcesMatch(
                trustAuthorization.ProjectRoot ?? trustAuthorization.WorkingDirectory,
                trustAuthorization.WorkingDirectory)
            && CommandExecutionPolicy.IsManifestFingerprintTrusted(
                trustAuthorization.ProjectRoot ?? trustAuthorization.WorkingDirectory);

        if (controls?.HarnessAuthorization is { IsolatedWorkspace: true, PipelineRole: "Actor" or "Planner" })
            tools.Add(GitSyncService.RepoStatusToolDefinition());
        if (controls?.HarnessAuthorization is { IsolatedWorkspace: true })
            tools.Add(SuggestCommandPrefixesToolDefinition());

        if (controls?.HarnessAuthorization is { IsolatedWorkspace: true } commandAuthorization
            && NativeAgentToolPolicy.IsRunCommandAuthorized(commandAuthorization))
        {
            // Read-only service controls remain available even when new command execution is disabled.
            tools.Add(NativeServiceManager.StatusToolDefinition());
            tools.Add(NativeServiceManager.LogsToolDefinition());
            tools.Add(NativeServiceManager.StopToolDefinition());
            tools.Add(NativeCommandRunner.RunLogToolDefinition());
            if (commandPolicy != CommandExecutionPolicy.Off)
            {
                tools.Add(NativeCommandRunner.ToolDefinition());
                tools.Add(NativeServiceManager.StartToolDefinition());
            }
        }

        if (string.Equals(agentMode, "plan", StringComparison.Ordinal))
        {
            if (controls?.HarnessAuthorization?.PipelineRole is not null)
                tools = NativeAgentToolPolicy.FilterDeniedTools(tools, PipelineToolPolicy.PipelinePlanDeniedTools);
            else
                tools = NativeAgentToolPolicy.FilterPlanTools(tools);
        }

        return new NativeAgentToolCatalogResult(tools, manifestFingerprint, manifestTrustedAtTurnStart);
    }

    private static JsonObject SuggestCommandPrefixesToolDefinition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "suggest_command_prefixes",
            ["description"] = "Read project manifests and GitHub Actions workflows and propose safe argv command prefixes for human review. This tool never executes commands and is available in Plan mode and when commandPolicy is Off. You may also include additionalPrefixes for commands needed by a non-standard build; shell operators, paths outside the worktree, blocked executables and global installs are rejected.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["additionalPrefixes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = 20,
                        ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 240 },
                        ["description"] = "Optional argv prefixes such as 'gradle test' or 'npm run integration'. Do not include shell syntax."
                    }
                },
                ["required"] = new JsonArray(),
                ["additionalProperties"] = false
            }
        }
    };

}
