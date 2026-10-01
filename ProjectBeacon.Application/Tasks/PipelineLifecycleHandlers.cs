namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;


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
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Planner, task.Id, ct);
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


public class LaunchSessionHandler : ICommandHandler<LaunchSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public LaunchSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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


public class ApprovePipelineHandler : ICommandHandler<ApprovePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ApprovePipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
    private readonly IBeaconDbFactory _dbFactory;

    public ForceClosePipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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


