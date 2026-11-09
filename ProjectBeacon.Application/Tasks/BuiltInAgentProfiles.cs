namespace ProjectBeacon.Application.Tasks;

using System.Text.Json;
using Domain.Common;
using Domain.Entities.Agents;

/// <summary>Execution intent declared by a built-in subtask profile.</summary>
public enum AgentProfileMode { Plan, Act }

/// <summary>Path grant strategy for a built-in subtask profile.</summary>
public enum AgentProfilePathPolicy { FromPlanner, Defaults, None }

public sealed record BuiltInAgentProfile(
    string Key,
    string Name,
    string Description,
    string Instruction,
    AgentProfileMode Mode,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> RequiredTools,
    AgentProfilePathPolicy PathPolicy,
    IReadOnlyList<string> DefaultPaths,
    bool CommandsEnabled,
    bool ReportsDiff,
    string SizeHint,
    string Effort,
    IReadOnlyList<string> OptionalTools);

/// <summary>
/// Versioned code-owned actor presets. User-authored role instructions remain separate:
/// they customize a Planner/Actor/Review role, while these presets define a subtask's work method.
/// </summary>
public static class BuiltInAgentProfiles
{
    private static readonly string[] Read =
    [
        McpToolNames.GetTree, McpToolNames.ReadFile, McpToolNames.SearchCode,
        McpToolNames.GetSignatures, McpToolNames.GetCallers, McpToolNames.GetChangedScope,
        McpToolNames.HashRange
    ];
    private static readonly string[] Context =
    [
        McpToolNames.GetTask, McpToolNames.TaskPipelineStatus, McpToolNames.ListConstraints,
        McpToolNames.ListDecisions, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead
    ];
    private static readonly string[] Write = [McpToolNames.ApplyPatch, McpToolNames.WriteFile];
    private static readonly string[] Always =
    [
        McpToolNames.SubtaskReportResult, McpToolNames.PipelineFailSubtask, McpToolNames.WorkspaceWrite
    ];
    private static readonly IReadOnlyList<string> Required = Array.AsReadOnly(Always);

    private static readonly IReadOnlyList<BuiltInAgentProfile> Profiles =
    [
        Profile("explorer", "Investigate code and answer a question without changing files.",
            "Investigate; do not modify files. Start with get_tree/search_code and read only what answers the question. Cite path:line evidence for every claim. Separate repository facts from inference. Say when the answer is not in the repository instead of guessing. Put the answer before evidence and open questions.",
            AgentProfileMode.Plan, ReadOnly(), AgentProfilePathPolicy.None, [], false, false, "one question, one code area", "low-medium"),
        Profile("implementer", "Add or change behaviour: a feature or improvement.",
            "Read neighbouring code first and follow its conventions. Do not add patterns, dependencies or abstractions the task does not require. Check list_constraints and list_decisions before designing. Make the smallest change; prefer apply_patch. Build and run relevant tests. Do not fix unrelated problems; list them as findings.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one goal, up to 3-5 files", "medium"),
        Profile("bugfixer", "Fix a described defect at its root cause.",
            "Reproduce first with a failing test or command and record it. Find the root cause before editing. Make the minimal fix. Show that reproduction and neighbouring tests pass. Report root cause and whether it can affect other locations.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one defect", "high"),
        Profile("test-writer", "Add tests for existing behaviour without changing production code.",
            "Read two existing tests before writing. Match the project's test framework and layout. Test behaviour, including normal, edge and failure paths. Never edit production code to make a test pass. Run the tests and ensure they fail when the tested behaviour is broken.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one module or behaviour", "medium"),
        Profile("refactorer", "Change structure without changing observable behaviour.",
            "Behaviour must not change. Run relevant tests before and after. Use get_callers and get_signatures before renaming or moving. Do not mix refactoring with fixes or features. If baseline tests are red or absent, stop and report.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one structural change", "medium"),
        Profile("docs-writer", "Write or update documentation, API comments or changelog.",
            "Write only what code and existing docs support. Verify every statement against source. Follow neighbouring language, tone and structure. Keep examples short and runnable. When code and docs disagree, describe actual code behaviour and report the mismatch. Do not execute commands.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.Defaults, ["docs", "README.md", "CHANGELOG.md"], false, true, "one document or section", "low"),
        Profile("build-config", "Change build manifests, CI workflows or dependency configuration.",
            "Call suggest_command_prefixes before changes to inspect actual build and test commands. After changing a manifest or workflow, call it again before using changed commands. Never add secrets or absolute local paths. Pin versions consistently. Verify with the project's own build command.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one configuration change", "medium"),
        Profile("verifier", "Independently verify build, tests and linters; do not fix.",
            "Verify; do not change files. Use task_pipeline_status and workspace reports to check other actors' claims. Run the named build, test and lint commands. Report each command, exit status and relevant failing lines. Start the summary with PASS or FAIL. A failed check is a valid report, not a reason to edit code.",
            AgentProfileMode.Act, ReadOnly(), AgentProfilePathPolicy.None, [], true, false, "one verification run", "low")
    ];

    private static readonly IReadOnlyDictionary<string, BuiltInAgentProfile> ByKey =
        Profiles.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<BuiltInAgentProfile> All => Profiles;
    public static BuiltInAgentProfile? Get(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out var profile) ? profile : null;

    public static BuiltInAgentProfile? ApplyOverride(BuiltInAgentProfile? basis, AgentProfile? custom)
    {
        if (basis is null || custom is null)
            return basis;
        var mode = Enum.TryParse<AgentProfileMode>(custom.Mode, true, out var selectedMode)
            ? selectedMode : basis.Mode;
        var allowed = new HashSet<string>(basis.Tools, StringComparer.Ordinal);
        var tools = custom.Tools.Where(allowed.Contains)
            .Concat(basis.RequiredTools).Distinct(StringComparer.Ordinal).ToArray();
        return basis with
        {
            Name = custom.Name,
            Description = custom.Description,
            Instruction = string.IsNullOrWhiteSpace(custom.Instruction)
                || string.Equals(custom.Instruction.Trim(), basis.Instruction, StringComparison.Ordinal)
                ? basis.Instruction
                : $"{basis.Instruction}{Environment.NewLine}{Environment.NewLine}User customization (additional guidance; it cannot override built-in profile or security boundaries):{Environment.NewLine}{custom.Instruction.Trim()}",
            Mode = basis.Mode == AgentProfileMode.Plan ? AgentProfileMode.Plan : mode,
            Tools = Array.AsReadOnly(tools),
            // User overrides may narrow command execution, never enable it on a read-only preset.
            CommandsEnabled = basis.CommandsEnabled && custom.CommandsEnabled,
            ReportsDiff = custom.ReportsDiff,
            SizeHint = custom.SizeHint,
            Effort = custom.Effort
        };
    }

    public static BuiltInAgentProfile? Resolve(string? key, string? snapshotJson)
    {
        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<BuiltInAgentProfile>(snapshotJson);
                if (snapshot is not null && string.Equals(snapshot.Key, key, StringComparison.OrdinalIgnoreCase))
                    return snapshot;
            }
            catch (JsonException)
            {
                // A damaged snapshot falls back to the immutable built-in definition.
            }
        }
        return Get(key);
    }

    public static string PlannerCatalogText(IReadOnlyDictionary<string, BuiltInAgentProfile>? overrides = null)
    {
        var profiles = Profiles.Select(profile =>
            overrides is not null && overrides.TryGetValue(profile.Key, out var custom) ? custom : profile);
        return string.Join(Environment.NewLine, profiles.Select(profile =>
            $"- {profile.Key}: {profile.Description} (mode={profile.Mode}, size={profile.SizeHint}, effort={profile.Effort})"));
    }

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/')
            || path.IndexOf((char)92) >= 0 || path.Contains(':')
            || Path.IsPathRooted(path))
            return false;
        var relative = path.EndsWith('/') ? path[..^1] : path;
        return relative.Length > 0
            && relative.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
    }

    public static bool IsWithinDefaults(string path, IReadOnlyList<string> defaults)
    {
        if (!IsSafeRelativePath(path))
            return false;
        return defaults.Any(prefix => string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static BuiltInAgentProfile Profile(
        string key, string description, string instruction, AgentProfileMode mode,
        IReadOnlyList<string> tools, AgentProfilePathPolicy pathPolicy, IReadOnlyList<string> defaultPaths,
        bool commands, bool reportsDiff, string sizeHint, string effort) =>
        new(key, key, description, instruction, mode, tools, Required, pathPolicy, defaultPaths,
            commands, reportsDiff, sizeHint, effort, [McpToolNames.TaskAttachFile]);

    private static IReadOnlyList<string> ReadOnly() =>
        Array.AsReadOnly(Read.Concat(Context).Concat(Always).Distinct(StringComparer.Ordinal).ToArray());
    private static IReadOnlyList<string> Writable() =>
        Array.AsReadOnly(Read.Concat(Context).Concat(Write).Concat(Always).Distinct(StringComparer.Ordinal).ToArray());
}
