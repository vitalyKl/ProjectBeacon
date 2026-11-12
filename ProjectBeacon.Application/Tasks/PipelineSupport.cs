namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities.Agents;
using Application.Common;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Maps pipeline entities to DTOs and loads pipeline state. Also compares actor ids in constant time for the privileged close path.
/// </summary>
public static class PipelineMappers
{
    public static SubtaskDto ToDto(Subtask s)
    {
        var profile = BuiltInAgentProfiles.Resolve(s.ProfileKey, s.ProfileSnapshotJson);
        return new SubtaskDto(s.Id, s.Instructions, s.Status, s.DiffRef, s.Summary, s.ReopenCount,
            s.AllowedMcpTools, s.AllowedPaths, s.CreatedAt, s.UpdatedAt, s.TaskPhaseId,
            s.ProfileKey, s.WakePlannerAfter, profile?.Name, profile?.Mode.ToString() ?? "Act");
    }

    public static PipelineSessionDto ToDto(PipelineSession s) =>
        new(s.Id, s.TaskId, s.SubtaskId, s.Role, s.Status, s.ModelBackendId,
            s.LaunchSpec, s.PromptContext, s.LaunchedAt, s.ClosedAt, s.ActivityJson, s.LastActivityAt, s.FailureReason,
            HasExternalResult: s.ExternalSessionId is not null);

    public static ReviewVerdictDto ToDto(ReviewVerdict v) =>
        new(v.Id, v.TaskId, v.SubtaskId, v.Kind, v.Note, v.CreatedAt);

    public static PipelineStateDto ToState(
        TaskItem task,
        IReadOnlyList<Subtask> subtasks,
        IReadOnlyList<PipelineSession> sessions,
        IReadOnlyList<ReviewVerdict> verdicts) =>
        new(task.Id, task.Title, task.Status, task.PipelineStage, task.ReviewNotes,
            subtasks.Select(ToDto).ToList(),
            sessions.Select(ToDto).ToList(),
            verdicts.Select(ToDto).ToList());
}

internal static class PipelineSupport
{
    public static Task<TaskItem?> FindTaskAsync(IBeaconDb db, Guid taskId, CancellationToken ct)
        => db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);

    /// <summary>
    /// Adds a non-blocking proposal Inbox snapshot to pipeline close notes. Expired pending
    /// rows are excluded even when their durable status transition has not yet been materialized.
    /// </summary>
    public static async Task<string> AppendProposalInboxSummaryAsync(
        IBeaconDb db,
        Guid projectId,
        Guid taskId,
        string notes,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var pending = await db.Proposals.CountAsync(proposal =>
            proposal.ProjectId == projectId
            && proposal.TaskId == taskId
            && proposal.Status == ProposalStatus.Pending
            && proposal.ExpiresAt > now, ct);
        var conflicted = await db.Proposals.CountAsync(proposal =>
            proposal.ProjectId == projectId
            && proposal.TaskId == taskId
            && proposal.Status == ProposalStatus.Conflicted, ct);
        return notes.TrimEnd()
            + $"\n\nProposal Inbox at pipeline close (informational; does not block completion): {pending} Pending; {conflicted} Conflicted.";
    }

    public static Task<string?> RoleInstructionsAsync(
        IBeaconDb db, Guid projectId, PipelineRole role, CancellationToken ct)
        => db.RoleBindings.AsNoTracking()
            .Where(binding => binding.ProjectId == projectId && binding.Role == role)
            .Select(binding => binding.ProfileInstructions)
            .FirstOrDefaultAsync(ct);

    public static Task<string?> ProjectNameAsync(IBeaconDb db, Guid projectId, CancellationToken ct)
        => db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Enters the executing stage while the task is in <see cref="TaskPipelineStage.ReopenedForRevision"/>,
    /// confirming the stage when it is already <see cref="TaskPipelineStage.Executing"/>. Returns the
    /// number of updated rows; zero means a concurrent transition moved the stage to something else.
    /// </summary>
    public static Task<int> EnterExecutingClaimAsync(IBeaconDb db, Guid taskId, CancellationToken ct)
        => db.Tasks
            .Where(t => t.Id == taskId
                && (t.PipelineStage == TaskPipelineStage.ReopenedForRevision
                    || t.PipelineStage == TaskPipelineStage.Executing))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PipelineStage, TaskPipelineStage.Executing), ct);

    /// <summary>
    /// Rolls back a guarded stage claim made before an external side effect (spawn) failed:
    /// sets <paramref name="toStage"/> while the stage is still <paramref name="fromStage"/>.
    /// A concurrent transition that moved the stage first makes this a no-op.
    /// </summary>
    public static Task<int> RollbackStageAsync(
        IBeaconDb db, Guid taskId, TaskPipelineStage fromStage, TaskPipelineStage? toStage, CancellationToken ct)
        => db.Tasks
            .Where(t => t.Id == taskId && t.PipelineStage == fromStage)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PipelineStage, toStage), ct);

    public static async Task<PipelineStateDto> StateAsync(IBeaconDb db, TaskItem task, CancellationToken ct)
    {
        var subtasks = await db.Subtasks
            .Where(s => s.TaskId == task.Id)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        var sessions = await db.PipelineSessions
            .Where(s => s.TaskId == task.Id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        var verdicts = await db.ReviewVerdicts
            .Where(v => v.TaskId == task.Id)
            .OrderBy(v => v.CreatedAt).ThenBy(v => v.Id)
            .ToListAsync(ct);

        var queuedSessionIds = new HashSet<Guid>();
        var pendingPipelinePayloads = await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(c => c.ProjectId == task.ProjectId
                && c.Kind == WorkstationCommandKind.RunPipelineSession
                && c.Status == WorkstationCommandStatus.Pending)
            .Select(c => c.PayloadJson)
            .ToListAsync(ct);
        foreach (var payload in pendingPipelinePayloads)
        {
            try
            {
                using var command = JsonDocument.Parse(payload);
                if (command.RootElement.ValueKind == JsonValueKind.Object
                    && command.RootElement.TryGetProperty("sessionId", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(value.GetString(), out var queuedId))
                    queuedSessionIds.Add(queuedId);
            }
            catch (JsonException)
            {
                // Malformed legacy commands are not displayed as queued sessions.
            }
        }

        var runtimeDeviceIds = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == task.ProjectId)
            .Select(r => r.DeviceId)
            .Distinct()
            .ToListAsync(ct);
        var devices = runtimeDeviceIds.Count == 0
            ? new List<Domain.Entities.Devices.DaemonDevice>()
            : await db.DaemonDevices.IgnoreQueryFilters()
                .Where(d => runtimeDeviceIds.Contains(d.Id))
                .ToListAsync(ct);
        var nativeDevices = devices.Where(d => IsNativeRuntime(d.WorkstationJson)).ToList();
        var hasNativeWorkstation = nativeDevices.Count > 0;
        var hasOnlineNativeWorkstation = nativeDevices.Any(d => d.IsOnline(DateTime.UtcNow));

        var phases = await db.TaskPhases.Where(p => p.TaskId == task.Id).ToListAsync(ct);
        Guid? plannerBackendId = phases.Count > 0
            ? phases.FirstOrDefault(p => p.Key == "understand")?.ModelBackendId
            : await db.RoleBindings
                .Where(b => b.ProjectId == task.ProjectId && b.Role == PipelineRole.Planner)
                .Select(b => (Guid?)b.ModelBackendId)
                .FirstOrDefaultAsync(ct);
        var backendIds = sessions.Where(s => s.ModelBackendId is not null)
            .Select(s => s.ModelBackendId!.Value)
            .Append(plannerBackendId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var modelBackends = await db.LocalModelBackends.AsNoTracking()
            .Where(b => backendIds.Contains(b.Id))
            .Select(b => new ModelBackendSummary(b.Id, b.Name, b.ContextSize))
            .ToListAsync(ct);
        var modelBackendsById = modelBackends.ToDictionary(backend => backend.Id);
        var existingBackendIds = modelBackendsById.Keys.ToHashSet();

        string? startBlockedReason = null;
        if (!hasNativeWorkstation)
            startBlockedReason = "PipelineNoNativeWorkstation";
        else if (!hasOnlineNativeWorkstation)
            startBlockedReason = "PipelineNativeWorkstationOffline";
        else if (plannerBackendId is null)
            startBlockedReason = "PipelineNoPlannerModelConfigured";
        else if (!existingBackendIds.Contains(plannerBackendId.Value))
            startBlockedReason = "PipelinePlannerModelMissing";
        else if (modelBackendsById[plannerBackendId.Value].ContextSize <= 0)
            startBlockedReason = "PipelineContextSizeInvalid";

        var state = PipelineMappers.ToState(task, subtasks, sessions, verdicts);
        return state with
        {
            CanStart = startBlockedReason is null,
            StartBlockedReason = startBlockedReason,
            Sessions = sessions.Select(session =>
            {
                string? blockedReason = null;
                if (session.Status == SessionStatus.Ready)
                {
                    if (!hasNativeWorkstation)
                        blockedReason = "PipelineNoNativeWorkstation";
                    else if (!hasOnlineNativeWorkstation)
                        blockedReason = "PipelineNativeWorkstationOffline";
                    else if (session.ModelBackendId is null)
                        blockedReason = "PipelineNoModelConfigured";
                    else if (!existingBackendIds.Contains(session.ModelBackendId.Value))
                        blockedReason = "PipelineModelMissing";
                }

                ModelBackendSummary? backend = session.ModelBackendId is { } modelId
                    && modelBackendsById.TryGetValue(modelId, out var foundBackend)
                        ? foundBackend
                        : null;
                if (session.Status == SessionStatus.Ready && blockedReason is null)
                {
                    if (session.ModelBackendId is null)
                        blockedReason = "PipelineNoModelConfigured";
                    else if (backend is null)
                        blockedReason = "PipelineModelMissing";
                    else if (backend.ContextSize <= 0)
                        blockedReason = "PipelineContextSizeInvalid";
                }
                var subtask = session.SubtaskId is { } subtaskId
                    ? subtasks.FirstOrDefault(item => item.Id == subtaskId)
                    : null;
                return PipelineMappers.ToDto(session) with
                {
                    CanLaunch = session.Status == SessionStatus.Ready && blockedReason is null,
                    LaunchBlockedReason = blockedReason,
                    IsQueued = queuedSessionIds.Contains(session.Id),
                    ModelName = backend?.Name,
                    AgentMode = session.Role == PipelineRole.Actor && subtask is not null
                        ? PipelineMappers.ToDto(subtask).AgentMode
                        : "Act"
                };
            }).ToList()
        };
    }

    public static async Task<List<PipelineSession>> OpenSessionsAsync(
        IBeaconDb db, Guid taskId, PipelineRole? role, CancellationToken ct)
    {
        var sessions = await db.PipelineSessions
            .Where(s => s.TaskId == taskId
                && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        if (role is not null)
            sessions.RemoveAll(s => s.Role != role);
        return sessions;
    }

    public static async Task<int> StopActorSessionsAndCancelCommandsAsync(
        IBeaconDb db,
        Guid projectId,
        Guid taskId,
        Guid subtaskId,
        CancellationToken ct)
    {
        var sessions = await db.PipelineSessions.IgnoreQueryFilters()
            .Where(session => session.ProjectId == projectId
                && session.TaskId == taskId
                && session.Role == PipelineRole.Actor
                && session.SubtaskId == subtaskId)
            .ToListAsync(ct);

        foreach (var session in sessions.Where(session =>
            session.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
            session.Close();

        await db.SaveChangesAsync(ct);
        return await CancelPipelineCommandsAsync(db, projectId, sessions.Select(session => session.Id).ToArray(), ct);
    }

    public static async Task<int> CancelPipelineCommandsAsync(
        IBeaconDb db,
        Guid projectId,
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken ct)
    {
        if (sessionIds.Count == 0)
            return 0;

        var wantedSessions = sessionIds.ToHashSet();
        var candidates = await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(command => command.ProjectId == projectId
                && command.Kind == WorkstationCommandKind.RunPipelineSession
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running))
            .Select(command => new { command.Id, command.PayloadJson })
            .ToListAsync(ct);

        var commandIds = candidates
            .Where(candidate => TryReadPipelineSessionId(candidate.PayloadJson) is { } sessionId
                && wantedSessions.Contains(sessionId))
            .Select(candidate => candidate.Id)
            .ToArray();
        if (commandIds.Length == 0)
            return 0;

        var completedAt = DateTime.UtcNow;
        return await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(command => commandIds.Contains(command.Id)
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running))
            .ExecuteUpdateAsync(update => update
                .SetProperty(command => command.Status, WorkstationCommandStatus.Cancelled)
                .SetProperty(command => command.CompletedAt, completedAt)
                .SetProperty(command => command.LeaseExpiresAt, (DateTime?)null), ct);
    }

    public static async Task<int> CancelReviewCheckCommandsAsync(
        IBeaconDb db,
        Guid projectId,
        Guid taskId,
        CancellationToken ct)
    {
        var reviewRunIds = (await db.ReviewRuns.IgnoreQueryFilters()
            .Where(run => run.ProjectId == projectId && run.TaskId == taskId)
            .Select(run => run.Id)
            .ToListAsync(ct)).ToHashSet();
        if (reviewRunIds.Count == 0)
            return 0;

        var candidates = await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(command => command.ProjectId == projectId
                && command.Kind == WorkstationCommandKind.RunReviewCheck
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running))
            .Select(command => new { command.Id, command.PayloadJson })
            .ToListAsync(ct);

        var commandIds = candidates
            .Where(candidate => TryReadJsonGuid(candidate.PayloadJson, "reviewRunId") is { } runId
                && reviewRunIds.Contains(runId))
            .Select(candidate => candidate.Id)
            .ToArray();
        if (commandIds.Length == 0)
            return 0;

        var completedAt = DateTime.UtcNow;
        return await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(command => commandIds.Contains(command.Id)
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running))
            .ExecuteUpdateAsync(update => update
                .SetProperty(command => command.Status, WorkstationCommandStatus.Cancelled)
                .SetProperty(command => command.CompletedAt, completedAt)
                .SetProperty(command => command.LeaseExpiresAt, (DateTime?)null), ct);
    }

    private static Guid? TryReadJsonGuid(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? TryReadPipelineSessionId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("sessionId", out var sessionId)
                && sessionId.ValueKind == JsonValueKind.String
                && Guid.TryParse(sessionId.GetString(), out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsNativeRuntime(string? workstationJson)
    {
        if (string.IsNullOrWhiteSpace(workstationJson))
            return true;
        try
        {
            using var document = JsonDocument.Parse(workstationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            // The workstation UI treats a missing runtime field as native for legacy devices.
            // Keep server-side preflight and launch selection consistent with that default.
            return !document.RootElement.TryGetProperty("agentRuntime", out var runtime)
                || (runtime.ValueKind == JsonValueKind.String
                    && string.Equals(runtime.GetString(), "native", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static int MaxReopenCycles()
    {
        var raw = Environment.GetEnvironmentVariable("BEACON_MAX_REOPEN_CYCLES");
        return int.TryParse(raw, out var value) && value > 0 ? value : 3;
    }

    public static async Task<bool> IsPrivilegedAsync(IBeaconDb db, string actorId, CancellationToken ct)
    {
        var workerToken = Environment.GetEnvironmentVariable("BEACON_WORKER_TOKEN");
        if (!string.IsNullOrEmpty(workerToken) && FixedTimeEquals(actorId, workerToken))
            return true;
        if (!Guid.TryParse(actorId, out var userId))
            return false;
        return await db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin, ct);
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
    private sealed record ModelBackendSummary(Guid Id, string Name, int ContextSize);

}

