namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public static class PipelineMappers
{
    public static SubtaskDto ToDto(Subtask s) =>
        new(s.Id, s.Instructions, s.Status, s.DiffRef, s.Summary, s.ReopenCount,
            s.AllowedMcpTools, s.AllowedPaths, s.CreatedAt, s.UpdatedAt);

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
    public static Task<TaskItem?> FindTaskAsync(BeaconDbContext db, Guid taskId, CancellationToken ct)
        => db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);

    public static Task<string?> ProjectNameAsync(BeaconDbContext db, Guid projectId, CancellationToken ct)
        => db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

    public static async Task<PipelineStateDto> StateAsync(BeaconDbContext db, TaskItem task, CancellationToken ct)
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
        BeaconDbContext db, Guid taskId, PipelineRole? role, CancellationToken ct)
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

    public static async Task<bool> IsPrivilegedAsync(BeaconDbContext db, string actorId, CancellationToken ct)
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

public class StartPipelineHandler : ICommandHandler<StartPipelineCommand, Result<PipelineSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartPipelineHandler(IDbContextFactory<BeaconDbContext> dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Planner, ct);
        var projectName = await PipelineSupport.ProjectNameAsync(db, task.ProjectId, ct);

        try
        {
            task.StartPipeline();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineSessionDto>(ex.Message);
        }

        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Planner,
            SessionPrompts.Planner(task, projectName), null, spawned.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}

public class CreateSubtaskHandler : ICommandHandler<CreateSubtaskCommand, Result<SubtaskDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateSubtaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

        var subtask = Subtask.Create(
            instructions, task.Id, task.ProjectId,
            command.Request.AllowedMcpTools, command.Request.AllowedPaths);
        db.Subtasks.Add(subtask);

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

public class StartActorSessionHandler : ICommandHandler<StartActorSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartActorSessionHandler(IDbContextFactory<BeaconDbContext> dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartActorSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineSessionDto>("Subtask not found.");
        if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            return Result.Failure<PipelineSessionDto>("Subtask is already finished.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }
        }
        if (task.PipelineStage != TaskPipelineStage.Executing)
            return Result.Failure<PipelineSessionDto>($"Cannot start actor session in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        if (subtask.Status == SubtaskStatus.Pending)
            subtask.Start();

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Actor, ct);
        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Actor,
            SessionPrompts.Actor(subtask), subtask.Id, spawned.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}

public class LaunchSessionHandler : ICommandHandler<LaunchSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public LaunchSessionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineSessionDto>> HandleAsync(LaunchSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.PipelineSessions.FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        if (session is null)
            return Result.Failure<PipelineSessionDto>("Session not found.");

        var task = await PipelineSupport.FindTaskAsync(db, session.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }
        }

        try
        {
            session.Launch();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineSessionDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(PipelineMappers.ToDto(session));
    }
}

public class ReportSubtaskResultHandler : ICommandHandler<ReportSubtaskResultCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ReportSubtaskResultHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ReportSubtaskResultCommand command, CancellationToken ct = default)
    {
        var diffRef = command.Request.DiffRef?.Trim();
        var summary = command.Request.Summary?.Trim();
        if (string.IsNullOrWhiteSpace(diffRef) || string.IsNullOrWhiteSpace(summary))
            return Result.Failure<PipelineStateDto>("DiffRef and Summary are required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineStateDto>("Subtask not found.");
        if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            return Result.Failure<PipelineStateDto>("Subtask is already finished.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }
        }

        if (subtask.Status == SubtaskStatus.Pending)
            subtask.Start();

        try
        {
            subtask.ReportResult(diffRef, summary);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct))
            if (session.SubtaskId == subtask.Id)
                session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

public class FailSubtaskHandler : ICommandHandler<FailSubtaskCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public FailSubtaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(FailSubtaskCommand command, CancellationToken ct = default)
    {
        var reason = command.Request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<PipelineStateDto>("Reason is required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineStateDto>("Subtask not found.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }
        }

        subtask.ForceFail(reason);

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct))
            if (session.SubtaskId == subtask.Id)
                session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

public class StartReviewHandler : ICommandHandler<StartReviewCommand, Result<PipelineSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartReviewHandler(IDbContextFactory<BeaconDbContext> dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartReviewCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Executing)
            return Result.Failure<PipelineSessionDto>($"Cannot start review in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        var subtasks = await db.Subtasks
            .Where(s => s.TaskId == task.Id)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        if (subtasks.Count == 0)
            return Result.Failure<PipelineSessionDto>("No subtasks to review.");
        if (subtasks.Any(s => s.Status is SubtaskStatus.Pending or SubtaskStatus.InProgress))
            return Result.Failure<PipelineSessionDto>("All subtasks must be done or failed before review.");

        try
        {
            task.EnterReview();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineSessionDto>(ex.Message);
        }

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Review, ct);
        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Review,
            SessionPrompts.Review(task, subtasks), null, spawned.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}

public class RecordReviewVerdictHandler : ICommandHandler<RecordReviewVerdictCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RecordReviewVerdictHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(RecordReviewVerdictCommand command, CancellationToken ct = default)
    {
        var note = command.Request.Note?.Trim();
        if (string.IsNullOrWhiteSpace(note))
            return Result.Failure<PipelineStateDto>("Verdict note is required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Reviewing)
            return Result.Failure<PipelineStateDto>($"Cannot record verdict in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        var hasReviewSession = await db.PipelineSessions
            .AnyAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Review, ct);
        if (!hasReviewSession)
            return Result.Failure<PipelineStateDto>("No review session found for this task.");

        Guid? subtaskId = null;
        if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
        {
            if (command.Request.SubtaskId is null)
                return Result.Failure<PipelineStateDto>("SubtaskId is required for a ReopenSubtask verdict.");

            var subtask = await db.Subtasks
                .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId.Value && s.TaskId == task.Id, ct);
            if (subtask is null)
                return Result.Failure<PipelineStateDto>("Subtask not found.");

            var maxCycles = PipelineSupport.MaxReopenCycles();
            if (subtask.ReopenCount >= maxCycles)
                subtask.ForceFail($"Reopen limit ({maxCycles}) exceeded: {note}");
            else
                subtask.Reopen(note);
            subtaskId = subtask.Id;
        }

        var verdict = ReviewVerdict.Create(task.Id, task.ProjectId, command.Request.Kind, note, subtaskId);
        db.ReviewVerdicts.Add(verdict);

        try
        {
            task.SetApproved();
            if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
                task.ReopenForRevision();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Review, ct))
            session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

public class ApprovePipelineHandler : ICommandHandler<ApprovePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ApprovePipelineHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ApprovePipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<PipelineStateDto>("Pipeline is not approved.");

        var notes = command.Request.Note;
        if (string.IsNullOrWhiteSpace(notes))
        {
            notes = await db.ReviewVerdicts
                .Where(v => v.TaskId == task.Id && v.Kind == ReviewVerdictKind.Approve)
                .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
                .Select(v => v.Note)
                .FirstOrDefaultAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(notes))
            return Result.Failure<PipelineStateDto>("No review notes available to close the pipeline.");

        try
        {
            task.ClosePipeline(notes);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, null, ct))
            session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

public class ForceClosePipelineHandler : ICommandHandler<ForceClosePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ForceClosePipelineHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ForceClosePipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        if (!await PipelineSupport.IsPrivilegedAsync(db, command.Request.ActorId, ct))
            return Result.Failure<PipelineStateDto>("Force close requires admin privileges.");

        var reason = command.Request.Reason?.Trim();
        var marker = string.IsNullOrEmpty(reason)
            ? "force-closed without review"
            : $"force-closed without review: {reason}";

        try
        {
            task.ClosePipeline(marker);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, null, ct))
            session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

public class GetPipelineHandler : ICommandHandler<GetPipelineCommand, Result<PipelineStateDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetPipelineHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(GetPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}
