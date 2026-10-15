namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
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
    public static SubtaskDto ToDto(Subtask s) =>
        new(s.Id, s.Instructions, s.Status, s.DiffRef, s.Summary, s.ReopenCount,
            s.AllowedMcpTools, s.AllowedPaths, s.CreatedAt, s.UpdatedAt, s.TaskPhaseId);

    public static PipelineSessionDto ToDto(PipelineSession s) =>
        new(s.Id, s.TaskId, s.SubtaskId, s.Role, s.Status, s.ModelBackendId,
            s.LaunchSpec, s.PromptContext, s.LaunchedAt, s.ClosedAt);

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
        return PipelineMappers.ToState(task, subtasks, sessions, verdicts);
    }

    public static async Task<List<PipelineSession>> OpenSessionsAsync(
        IBeaconDb db, Guid taskId, PipelineRole? role, CancellationToken ct)
    {
        var sessions = await db.PipelineSessions
            .Where(s => s.TaskId == taskId
                && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        if (role is not null)
            sessions.RemoveAll(s => s.Role != role);
        return sessions;
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
}

