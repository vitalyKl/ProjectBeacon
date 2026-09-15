namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record CompileBriefRequest(
    Guid ProjectId,
    Guid? RepoId,
    string? Path,
    Guid? TaskId,
    int? BudgetTokens,
    bool IncludeHandoff,
    bool IncludeChangedScope,
    bool IncludeTreeCapsule);

public record CompileBriefCommand(CompileBriefRequest Request) : ICommand<Result<CompileBriefResult>>;

public record CompileBriefResult(
    string SchemaVersion,
    string CompilerVersion,
    string ProjectId,
    string ProjectName,
    string CompiledAt,
    string RevisionId,
    string CompiledHash,
    string BriefMarkdown,
    int TokenEstimate,
    bool BudgetOverflow,
    IList<string> DroppedSections);

public class CompileBriefHandler
{
    private static readonly string[] NeverDropSections =
        ["non_goals", "security", "definition_of_done"];

    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CompileBriefHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<CompileBriefResult>> HandleAsync(CompileBriefCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == command.Request.ProjectId, ct);

        if (project is null)
            return Result.Failure<CompileBriefResult>("Project not found.");

        var budget = command.Request.BudgetTokens ?? 8000;

        var nodes = await db.ContextSections
            .Where(s =>
                s.ProjectId == command.Request.ProjectId &&
                (command.Request.RepoId == null || s.RepoId == command.Request.RepoId || s.RepoId == null))
            .ToListAsync(ct);

        var sections = MergeSections(nodes);
        var ordered = OrderSections(sections);

        TaskItem? task = null;
        if (command.Request.TaskId is { } taskId)
        {
            task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.ProjectId == command.Request.ProjectId, ct);
        }

        var constraints = await db.Constraints
            .Where(c => c.ProjectId == command.Request.ProjectId && c.Status == ConstraintStatus.Active)
            .ToListAsync(ct);

        var decisions = await db.Decisions
            .Where(d => d.ProjectId == command.Request.ProjectId && d.Status == DecisionStatus.Accepted)
            .ToListAsync(ct);

        var briefParts = new List<(string Id, string Markdown, bool NeverDrop)>();

        if (task is not null)
        {
            var desc = string.IsNullOrWhiteSpace(task.Description) ? task.Title : $"{task.Title}\n\n{task.Description}";
            briefParts.Add(("task_description", $"## Task\n\n{desc}", true));
        }

        foreach (var section in ordered.Where(s => NeverDropSections.Contains(s.SectionId)))
        {
            briefParts.Add((section.SectionId, $"## {section.Title}\n\n{section.BodyMarkdown}", true));
        }

        if (constraints.Count > 0)
        {
            var constraintLines = constraints.Select(c => $"- [{c.Kind}] {c.Body}");
            var contradiction = FindContradiction(constraints);
            var body = string.Join("\n", constraintLines);
            if (contradiction is not null)
                body += $"\n\nWarning: conflicting constraints on `{contradiction}` (Must vs MustNot).";
            briefParts.Add(("constraints", $"## Constraints\n\n{body}", true));
        }

        if (decisions.Count > 0)
        {
            var decisionBlocks = decisions.Select(d =>
            {
                var consequences = string.IsNullOrWhiteSpace(d.Consequences)
                    ? ""
                    : $"\n\nConsequences: {d.Consequences}";
                return $"### {d.Title}\n\n{d.DecisionBody}{consequences}";
            });
            briefParts.Add(("decisions", "## Decisions\n\n" + string.Join("\n\n", decisionBlocks), false));
        }

        briefParts.Add(("tools",
            "## Tools for this task\n\n- `context_compile` — compile this brief\n- `claim_task` / `finish_work` — task lifecycle\n- Beacon file tools (when the local daemon is connected)",
            true));

        var includeTree = command.Request.IncludeTreeCapsule || command.Request.RepoId != null;
        var includeChanged = command.Request.IncludeChangedScope || command.Request.RepoId != null;
        if (includeTree)
        {
            briefParts.Add(("tree_capsule",
                "## Tree\n\nRepo is linked but the code index is not in this process yet (Phase 7). Prefer Beacon file tools over guessing paths.",
                false));
        }

        if (includeChanged)
        {
            briefParts.Add(("changed_scope",
                "## Changed scope\n\nNo local index is attached. Re-query after the daemon indexes the working tree.",
                false));
        }

        foreach (var section in ordered.Where(s => !NeverDropSections.Contains(s.SectionId)))
        {
            briefParts.Add((section.SectionId, $"## {section.Title}\n\n{section.BodyMarkdown}", false));
        }

        var kept = new List<string>();
        var dropped = new List<string>();
        var usedTokens = 0;

        foreach (var (id, markdown, neverDrop) in briefParts)
        {
            var tokens = Tokenizer.CountTokens(markdown);
            if (neverDrop || usedTokens + tokens <= budget)
            {
                kept.Add(markdown);
                usedTokens += tokens;
            }
            else
            {
                dropped.Add(id);
            }
        }

        var briefMarkdown = $"# {project.Name}\n\n" + string.Join("\n\n", kept);
        var totalTokens = Tokenizer.CountTokens(briefMarkdown);
        var overflow = totalTokens > budget || dropped.Count > 0;

        var compiledHash = ComputeHash(briefMarkdown);
        var revision = ContextRevision.Create(
            compiledHash: compiledHash,
            briefMarkdown: briefMarkdown,
            briefJson: "",
            tokenEstimate: totalTokens,
            projectId: command.Request.ProjectId,
            sourceNodeIds: nodes.Select(n => n.Id).ToList(),
            targetRepoId: command.Request.RepoId?.ToString(),
            targetPath: command.Request.Path,
            targetTaskId: command.Request.TaskId?.ToString());

        db.ContextRevisions.Add(revision);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CompileBriefResult(
            SchemaVersion: "1",
            CompilerVersion: "1.0.0",
            ProjectId: project.Id.ToString(),
            ProjectName: project.Name,
            CompiledAt: DateTime.UtcNow.ToString("o"),
            RevisionId: revision.Id.ToString(),
            CompiledHash: compiledHash,
            BriefMarkdown: briefMarkdown,
            TokenEstimate: totalTokens,
            BudgetOverflow: overflow,
            DroppedSections: dropped));
    }

    private static string? FindContradiction(IReadOnlyList<Constraint> constraints)
    {
        foreach (var group in constraints.GroupBy(c => c.ScopePath ?? string.Empty))
        {
            var kinds = group.Select(c => c.Kind).ToHashSet();
            if (kinds.Contains(ConstraintKind.Must) && kinds.Contains(ConstraintKind.MustNot))
                return group.Key.Length == 0 ? "(project)" : group.Key;
        }

        return null;
    }

    private static IList<ContextSection> MergeSections(IList<ContextSection> nodes)
    {
        var merged = new Dictionary<string, ContextSection>();

        foreach (var node in nodes)
        {
            var key = node.SectionId;
            if (key == "custom" && !string.IsNullOrEmpty(node.Key))
                key = $"custom:{node.Key}";

            if (!merged.TryGetValue(key, out var existing) || node.UpdatedAt >= existing.UpdatedAt)
                merged[key] = node;
        }

        return merged.Values.ToList();
    }

    private static IList<ContextSection> OrderSections(IList<ContextSection> sections)
    {
        var priorityOrder = new Dictionary<string, int>
        {
            { "pitfalls", 0 },
            { "conventions", 1 },
            { "architecture", 2 },
            { "commands", 3 },
            { "goals", 4 },
            { "stack", 5 },
            { "style", 6 },
            { "glossary", 7 },
            { "ownership", 8 }
        };

        return sections.OrderBy(s =>
        {
            if (NeverDropSections.Contains(s.SectionId))
                return -1000 + Array.IndexOf(NeverDropSections, s.SectionId);

            return priorityOrder.TryGetValue(s.SectionId, out var prio) ? prio : 1000;
        }).ToList();
    }

    private static string ComputeHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}