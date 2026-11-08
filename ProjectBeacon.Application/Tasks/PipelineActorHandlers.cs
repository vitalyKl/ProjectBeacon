namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Domain.Common;
using Domain.Entities.Agents;
using Domain.Entities.Tasks;
using System.Text.Json;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a subtask. Instructions are required, and the task pipeline must already be started.
/// </summary>
public class CreateSubtaskHandler : ICommandHandler<CreateSubtaskCommand, Result<SubtaskDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CreateSubtaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<SubtaskDto>> HandleAsync(CreateSubtaskCommand command, CancellationToken ct = default)
    {
        var instructions = command.Request.Instructions?.Trim();
        if (string.IsNullOrWhiteSpace(instructions))
            return Result.Failure<SubtaskDto>("Instructions are required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<SubtaskDto>("Task not found.");

        if (task.PipelineStage is not (TaskPipelineStage.Planning or TaskPipelineStage.Executing))
            return Result.Failure<SubtaskDto>(task.PipelineStage is null
                ? "Pipeline is not started."
                : $"Cannot create subtask in stage {task.PipelineStage}.");

        IReadOnlyList<string>? allowedTools = command.Request.AllowedMcpTools;
        IReadOnlyList<string>? allowedPaths = command.Request.AllowedPaths;
        var optionalTools = command.Request.OptionalMcpTools ?? [];
        var profileKey = command.Request.Profile?.Trim();
        BuiltInAgentProfile? resolvedProfile = null;
        if (!string.IsNullOrWhiteSpace(profileKey))
        {
            var profile = BuiltInAgentProfiles.Get(profileKey);
            if (profile is null)
                return Result.Failure<SubtaskDto>(
                    $"Unknown subtask profile '{profileKey}'. Available profiles: {string.Join(", ", BuiltInAgentProfiles.All.Select(p => p.Key))}.");

            AgentProfile? customProfile = null;
            if (command.Actor?.UserId is { } profileOwnerId)
                customProfile = await db.AgentProfiles.FirstOrDefaultAsync(
                    profileRow => profileRow.UserId == profileOwnerId && profileRow.Key == profileKey, ct);
            profile = BuiltInAgentProfiles.ApplyOverride(profile, customProfile)!;
            resolvedProfile = profile;

            var defaults = profile.Tools;
            var optionalGrants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var optionalTool in optionalTools)
            {
                if (!McpToolCatalog.TryResolve(optionalTool, out var optionalDescriptor)
                    || !profile.OptionalTools.Contains(optionalDescriptor!.Name, StringComparer.Ordinal))
                    return Result.Failure<SubtaskDto>(
                        $"Tool '{optionalTool}' is not an optional grant for profile '{profile.Key}'.");
                optionalGrants.Add(optionalDescriptor!.Name);
            }
            if (allowedTools is { Count: > 0 })
            {
                var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var toolName in allowedTools)
                {
                    if (!McpToolCatalog.TryResolve(toolName, out var descriptor)
                        || !defaults.Contains(descriptor!.Name, StringComparer.Ordinal))
                        return Result.Failure<SubtaskDto>(
                            $"Tool '{toolName}' is outside profile '{profile.Key}'. A Planner may narrow, but cannot expand, its tool grants.");
                    requested.Add(descriptor!.Name);
                }
                allowedTools = defaults.Where(name => requested.Contains(name)
                    || profile.RequiredTools.Contains(name, StringComparer.Ordinal))
                    .Concat(optionalGrants).Distinct(StringComparer.Ordinal).ToArray();
            }
            else
                allowedTools = defaults.Concat(optionalGrants).Distinct(StringComparer.Ordinal).ToArray();

            if (allowedPaths?.Any(path => !BuiltInAgentProfiles.IsSafeRelativePath(path)) == true)
                return Result.Failure<SubtaskDto>(
                    "allowedPaths must contain safe repository-relative paths without absolute roots or traversal segments.");

            switch (profile.PathPolicy)
            {
                case AgentProfilePathPolicy.FromPlanner:
                    if (allowedPaths is not { Count: > 0 })
                        return Result.Failure<SubtaskDto>(
                            $"Profile '{profile.Key}' requires explicit allowedPaths from the Planner.");
                    break;
                case AgentProfilePathPolicy.Defaults:
                    if (allowedPaths is not { Count: > 0 })
                        allowedPaths = profile.DefaultPaths;
                    else if (allowedPaths.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, profile.DefaultPaths)))
                        return Result.Failure<SubtaskDto>(
                            $"Profile '{profile.Key}' allows only paths beneath: {string.Join(", ", profile.DefaultPaths)}.");
                    break;
                case AgentProfilePathPolicy.None:
                    allowedPaths ??= [];
                    break;
            }
        }
        else if (optionalTools.Count > 0)
        {
            return Result.Failure<SubtaskDto>("optionalMcpTools requires a built-in profile.");
        }

        var subtask = Subtask.Create(
            instructions, task.Id, task.ProjectId,
            allowedTools, allowedPaths, profileKey, command.Request.WakePlannerAfter,
            resolvedProfile is null ? null : JsonSerializer.Serialize(resolvedProfile));
        var phases = await db.TaskPhases.Where(p => p.TaskId == task.Id).OrderBy(p => p.SortOrder).ToListAsync(ct);
        var phase = phases.FirstOrDefault(p => p.Status == TaskPhaseStatus.Active)
            ?? phases.FirstOrDefault(p => string.Equals(p.Key, "do", StringComparison.Ordinal));
        if (phase is not null)
            subtask.AssignPhase(phase.Id);
        db.Subtasks.Add(subtask);

        // Unguarded stage write by design: this is the only Planning→Executing claimant
        // and never touches StateRevision, so it cannot create a second finish-work winner.
        // A concurrent finish_work that commits first may leave the stage active on a Done
        // task; pipeline claims re-check the current status before writing.
        try
        {
            task.EnterExecuting();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<SubtaskDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(PipelineMappers.ToDto(subtask));
    }
}
