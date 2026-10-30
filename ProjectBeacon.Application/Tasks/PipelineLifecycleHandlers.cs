namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Agents;
using Application.Common;
using Application.Devices;
using Domain.Entities.Tasks;
using Domain.Entities.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Starts the pipeline and creates the planner session.
/// </summary>
public class StartPipelineHandler : ICommandHandler<StartPipelineCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartPipelineHandler(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        var expectedStatus = task.Status;
        try
        {
            task.StartPipeline();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineSessionDto>(ex.Message);
        }

        // Guarded stage claim before the spawn: only takes effect if the pipeline is not started.
        var claimed = await db.Tasks
            .Where(t => t.Id == task.Id && t.PipelineStage == null && t.Status == expectedStatus)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PipelineStage, task.PipelineStage)
                .SetProperty(t => t.Status, task.Status), ct);
        if (claimed == 0)
            return Result.Failure<PipelineSessionDto>("Pipeline state changed while starting; retry.");

        SpawnedSession? spawned = null;
        Exception? spawnFailure = null;
        try
        {
            spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Planner, task.Id, ct);
        }
        catch (Exception ex)
        {
            spawnFailure = ex;
        }
        if (spawnFailure is not null)
        {
            await PipelineSupport.RollbackStageAsync(db, task.Id, TaskPipelineStage.Planning, null, ct);
            if (spawnFailure is OperationCanceledException canceled && ct.IsCancellationRequested)
                throw canceled;
            return Result.Failure<PipelineSessionDto>(spawnFailure.Message);
        }

        var projectName = await PipelineSupport.ProjectNameAsync(db, task.ProjectId, ct);
        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Planner,
            SessionPrompts.Planner(task, projectName), null, spawned!.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}
/// <summary>
/// Marks a Ready pipeline session launched.
/// </summary>
public class LaunchSessionHandler : ICommandHandler<LaunchSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public LaunchSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineSessionDto>> HandleAsync(LaunchSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Reset state before a strategy retry and keep reads, state transition, and command insert
            // in the same transaction. This is required by Npgsql's retrying execution strategy.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var session = await db.PipelineSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
            if (session is null)
                return Result.Failure<PipelineSessionDto>("Session not found.");

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == session.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
            {
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineSessionDto>("Pipeline state changed while launching; retry.");
            }

            WorkstationCommand? pipelineCommand = null;
            var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(r => r.ProjectId == session.ProjectId)
                .ToListAsync(ct);
            if (runtimes.Count > 0)
            {
                var deviceIds = runtimes.Select(r => r.DeviceId).Distinct().ToArray();
                var devices = await db.DaemonDevices.IgnoreQueryFilters()
                    .Where(d => deviceIds.Contains(d.Id))
                    .ToListAsync(ct);
                var candidates = runtimes
                    .Join(devices, r => r.DeviceId, d => d.Id, (runtime, device) => new { Runtime = runtime, Device = device })
                    .Where(x => IsNativeRuntime(x.Device.WorkstationJson))
                    .ToList();

                if (candidates.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    var selected = candidates
                        .Where(x => x.Device.IsOnline(now))
                        .OrderByDescending(x => x.Device.LastHeartbeatAt)
                        .FirstOrDefault();
                    if (selected is null)
                        return Result.Failure<PipelineSessionDto>("The native pipeline workstation is offline.");

                    if (session.ModelBackendId is not { } backendId)
                        return Result.Failure<PipelineSessionDto>("No model backend is configured for this pipeline session.");
                    var backend = await db.LocalModelBackends
                        .FirstOrDefaultAsync(b => b.Id == backendId, ct);
                    if (backend is null)
                        return Result.Failure<PipelineSessionDto>("The configured pipeline model backend was not found.");

                    var model = backend.BackendType == ModelBackendType.LlamaCpp || string.IsNullOrWhiteSpace(backend.OpenCodeModel)
                        ? $"{OpencodePayload.ProviderId}/{OpencodePayload.ModelKey(backend.Name)}"
                        : backend.OpenCodeModel.Trim();
                    var payload = JsonSerializer.Serialize(new
                    {
                        projectId = session.ProjectId,
                        taskId = session.TaskId,
                        subtaskId = session.SubtaskId,
                        sessionId = session.Id,
                        role = session.Role.ToString(),
                        title = $"{session.Role} · {task.Title}",
                        prompt = session.PromptContext,
                        model,
                        contextLimit = backend.ContextSize,
                        reasoningEffort = "medium"
                    });
                    pipelineCommand = WorkstationCommand.Create(
                        selected.Device.Id,
                        WorkstationCommandKind.RunPipelineSession,
                        payload,
                        session.ProjectId,
                        selected.Device.UserId);
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

            var launched = await db.PipelineSessions
                .Where(s => s.Id == session.Id && s.Status == SessionStatus.Ready)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.Status, session.Status)
                    .SetProperty(s => s.ExternalSessionId, session.ExternalSessionId)
                    .SetProperty(s => s.LaunchedAt, session.LaunchedAt), ct);
            if (launched == 0)
                return Result.Failure<PipelineSessionDto>("Session state changed while launching; retry.");

            if (pipelineCommand is not null)
            {
                db.WorkstationCommands.Add(pipelineCommand);
                await db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return Result.Ok(PipelineMappers.ToDto(session));
        });
    }

    private static bool IsNativeRuntime(string? workstationJson)
    {
        if (string.IsNullOrWhiteSpace(workstationJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(workstationJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("agentRuntime", out var runtime)
                && runtime.ValueKind == JsonValueKind.String
                && string.Equals(runtime.GetString(), "native", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
/// <summary>
/// Closes an approved pipeline. Requires review notes and a completed review check.
/// </summary>
public class ApprovePipelineHandler : ICommandHandler<ApprovePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ApprovePipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ApprovePipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
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

        var runs = await db.ReviewRuns.Where(r => r.TaskId == task.Id).ToListAsync(ct);
        var proved = runs.Any(r => r.IsCheckProof());
        if (!proved)
            return Result.Failure<PipelineStateDto>("Close requires a completed review check.");

        var expectedStatus = task.Status;
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

        var updated = await db.Tasks
            .Where(t => t.Id == task.Id
                && t.PipelineStage == TaskPipelineStage.Approved
                && t.Status == expectedStatus)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PipelineStage, task.PipelineStage)
                .SetProperty(t => t.Status, task.Status)
                .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                .SetProperty(t => t.CompletedAt, task.CompletedAt), ct);
        if (updated == 0)
            return Result.Failure<PipelineStateDto>("Pipeline state changed while closing; retry.");

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}
/// <summary>
/// Closes a pipeline without that check. Fails unless the actor is privileged.
/// </summary>
public class ForceClosePipelineHandler : ICommandHandler<ForceClosePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ForceClosePipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ForceClosePipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        if (!await PipelineSupport.IsPrivilegedAsync(db, command.Request.ActorId, ct))
            return Result.Failure<PipelineStateDto>("Force close requires admin privileges.");

        var reason = command.Request.Reason?.Trim();
        var marker = string.IsNullOrEmpty(reason)
            ? "force-closed without review"
            : $"force-closed without review: {reason}";

        var expectedStatus = task.Status;
        var expectedStage = task.PipelineStage;
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

        var updated = await db.Tasks
            .Where(t => t.Id == task.Id && t.Status == expectedStatus && t.PipelineStage == expectedStage)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PipelineStage, task.PipelineStage)
                .SetProperty(t => t.Status, task.Status)
                .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                .SetProperty(t => t.CompletedAt, task.CompletedAt), ct);
        if (updated == 0)
            return Result.Failure<PipelineStateDto>("Pipeline state changed while force-closing; retry.");

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}


