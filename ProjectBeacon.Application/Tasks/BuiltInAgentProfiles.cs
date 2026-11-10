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
    IReadOnlyList<string> OptionalTools,
    string ApplyPolicy = "Review");

/// <summary>
/// Versioned code-owned actor presets. User-authored role instructions remain separate:
/// they customize a Planner/Actor/Review role, while these presets define a subtask's work method.
/// </summary>
public static class BuiltInAgentProfiles
{
    private static readonly IReadOnlyList<string> Read = PipelineToolPolicy.AgentProfileReadTools;
    private static readonly IReadOnlyList<string> Context = PipelineToolPolicy.AgentProfileContextTools;
    private static readonly IReadOnlyList<string> Write = PipelineToolPolicy.AgentProfileWriteTools;
    private static readonly IReadOnlyList<string> Always = PipelineToolPolicy.AgentProfileRequiredTools;
    private static readonly IReadOnlyList<string> Required = Always;

    private static readonly IReadOnlyList<BuiltInAgentProfile> Profiles =
    [
        Profile("explorer", "Investigate code and answer a question without changing files.",
            "Investigate; do not modify files. Start with get_tree/search_code and read only what answers the question. Cite path:line evidence for every claim. Separate repository facts from inference. Say when the answer is not in the repository instead of guessing. Put the answer before evidence and open questions.",
            AgentProfileMode.Act, ReadOnly(), AgentProfilePathPolicy.None, [], false, false, "one question, one code area", "low-medium"),
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
            "Write only what code and existing docs support. Verify every statement against source. Follow neighbouring language, tone and structure. Keep examples short and runnable. When code and docs disagree, describe actual code behaviour and report the mismatch. Do not execute commands or edit workspace files: documentation file changes must be submitted as RepoPatch proposals and must not enter the live working tree before approval. Read the complete original file, use hash_file for its exact baseHash, draft the complete proposed UTF-8 file content in memory, and use hash_text for resultHash. Submit knowledge_propose with targetKind RepoPatch, targetId equal to the project id, operation Update, baseRevision 1 (RepoPatch uses per-file hashes rather than a content revision), and payload { patch, files: [{ gitPath, projectPath, baseHash, resultHash }] }. The patch must be a valid unified diff whose paths match the manifest; gitPath is repository-relative: prefix it with the repository-relative project root from the isolated workspace environment; projectPath is relative to the project root. Use only the allowed paths granted by the docs-writer profile. Modify docs/ paths or the root README.md; do not write outside those allowed paths. For changes to existing revisioned project knowledge (project description, context section, decision, constraint, milestone, or task description), use the corresponding targetKind, current baseRevision, complete target-specific payload, and a concise Markdown rationale. Use knowledge_proposals to inspect your own proposals and knowledge_withdraw to withdraw an obsolete pending proposal. A proposal is not applied until a human accepts it (or an explicitly enabled RepoPatch Auto policy completes); never claim it was applied merely because submission succeeded. Never put credentials or secrets into proposals.",
            AgentProfileMode.Act, DocumentationWritable(), AgentProfilePathPolicy.Defaults, ["docs", "README.md"], false, true, "one document or section", "low"),
        Profile("build-config", "Change build manifests, CI workflows or dependency configuration.",
            "Call suggest_command_prefixes before changes to inspect actual build and test commands. After changing a manifest or workflow, call it again before using changed commands. Never add secrets or absolute local paths. Pin versions consistently. Verify with the project's own build command.",
            AgentProfileMode.Act, Writable(), AgentProfilePathPolicy.FromPlanner, [], true, true, "one configuration change", "medium"),
        Profile("verifier", "Independently verify build, tests and linters; do not fix.",
            "Verify; do not change files. Use task_pipeline_status, context nodes, decisions and constraints to check project knowledge and other actors' claims. Run the named build, test and lint commands. Report each command, exit status and relevant failing lines. Start the summary with PASS or FAIL. A failed check is a valid report, not a reason to edit code.",
            AgentProfileMode.Act, ReadOnly(), AgentProfilePathPolicy.None, [], true, false, "one verification run", "low"),
        Profile("knowledge-curator", "Prepare project knowledge updates for human review without accepting or activating them.",
            "Capture durable project knowledge for human review. Read the existing context, decisions, constraints, milestones, task descriptions, and project description first; use the returned revision values rather than guessing. For a new context draft, use context_create_draft. For a change to existing revisioned content, use knowledge_propose with the exact targetKind and targetId, current baseRevision, complete target-specific payload, and a concise Markdown rationale. P2 currently supports Update only. Use knowledge_proposals to check your own proposal status and human rejection note; use knowledge_withdraw only for your own obsolete pending proposal. Submission never applies a change: do not claim the content changed until a human accepts it. Never directly edit or delete reviewed context, accept/reject/withdraw another author's proposal, accept or deprecate decisions, supersede decisions, or activate/reject constraints. Distinguish evidence from inference, cite source paths or task references, and never include secrets or credentials.",
            AgentProfileMode.Act, KnowledgeWritable(), AgentProfilePathPolicy.None, [], false, false, "one focused knowledge update", "medium")
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
                ? basis.Instruction
                : $"{basis.Instruction}{Environment.NewLine}{Environment.NewLine}User customization (additional guidance; it cannot override built-in profile or security boundaries):{Environment.NewLine}{custom.Instruction}",
            Mode = basis.Mode == AgentProfileMode.Plan ? AgentProfileMode.Plan : mode,
            Tools = Array.AsReadOnly(tools),
            // User overrides may narrow command execution, never enable it on a read-only preset.
            CommandsEnabled = basis.CommandsEnabled && custom.CommandsEnabled,
            ReportsDiff = custom.ReportsDiff,
            SizeHint = custom.SizeHint,
            Effort = custom.Effort,
            ApplyPolicy = string.Equals(basis.Key, "docs-writer", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<Domain.Enums.AgentProfileApplyPolicy>(custom.ApplyPolicy, true, out var selectedPolicy)
                && Enum.IsDefined(selectedPolicy)
                    ? selectedPolicy.ToString()
                    : "Review"
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
        new(key, key, description,
            instruction + Environment.NewLine + Environment.NewLine
                + "Write long-form text and reports as Markdown, without HTML. Use headings no deeper than ##. "
                + "Final subtask reports must use these headings: ## Done, ## Findings, ## Changed, ## Verified and ## Left.",
            mode, tools, Required, pathPolicy, defaultPaths,
            commands, reportsDiff, sizeHint, effort, [McpToolNames.TaskAttachFile]);

    private static IReadOnlyList<string> ReadOnly() =>
        Array.AsReadOnly(Read.Concat(Context).Concat(Always).Distinct(StringComparer.Ordinal).ToArray());
    private static IReadOnlyList<string> Writable() =>
        Array.AsReadOnly(Read.Concat(Context).Concat(Write).Concat(Always).Distinct(StringComparer.Ordinal).ToArray());
    private static IReadOnlyList<string> KnowledgeWritable() =>
        Array.AsReadOnly(Read.Concat(Context).Concat(PipelineToolPolicy.AgentProfileKnowledgeWriteTools)
            .Concat(Always).Distinct(StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<string> DocumentationWritable() =>
        Array.AsReadOnly(ReadOnly()
            .Concat(PipelineToolPolicy.AgentProfileKnowledgeWriteTools)
            .Concat(new[] { McpToolNames.HashFile, McpToolNames.HashText })
            .Distinct(StringComparer.Ordinal).ToArray());
}
