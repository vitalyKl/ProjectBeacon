namespace ProjectBeacon.Application.Evals;

using Common;
using Domain.Entities.Evals;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Starts an eval run for a task under WithBrief or WithoutBrief. PairId groups the two runs.
/// </summary>
public record RecordEvalRunRequest(Guid ProjectId, Guid TaskId, string? PairId, EvalCondition Condition);
/// <summary>
/// Command for record eval run.
/// </summary>
public record RecordEvalRunCommand(RecordEvalRunRequest Request) : ICommand<Result<EvalRunDto>>;
/// <summary>
/// Stores token counts, turns, check exit code, and transcript for an eval run.
/// </summary>
public record CompleteEvalRunRequest(
    Guid EvalRunId,
    int PromptTokens,
    int CompletionTokens,
    int TurnCount,
    int? CheckExitCode,
    string? TranscriptRef,
    string? CheckOutput = null);
/// <summary>
/// Command for complete eval run.
/// </summary>
public record CompleteEvalRunCommand(CompleteEvalRunRequest Request) : ICommand<Result<EvalRunDto>>;
/// <summary>
/// Fields for list eval runs.
/// </summary>
public record ListEvalRunsRequest(Guid ProjectId, Guid? TaskId = null);
/// <summary>
/// One eval run, including token counts and whether the check passed.
/// </summary>
public record EvalRunDto(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    string? PairId,
    string Condition,
    int PromptTokens,
    int CompletionTokens,
    int TurnCount,
    bool? Passed,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? TranscriptRef);
/// <summary>
/// Starts an eval run for a task under one condition.
/// </summary>
public class RecordEvalRunHandler : ICommandHandler<RecordEvalRunCommand, Result<EvalRunDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RecordEvalRunHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<EvalRunDto>> HandleAsync(RecordEvalRunCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var run = EvalRun.Create(command.Request.ProjectId, command.Request.TaskId, command.Request.PairId, command.Request.Condition);
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Result.Ok(EvalRunMapper.Map(run));
    }
}
/// <summary>
/// Stores the outcome of an eval run.
/// </summary>
public class CompleteEvalRunHandler : ICommandHandler<CompleteEvalRunCommand, Result<EvalRunDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CompleteEvalRunHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<EvalRunDto>> HandleAsync(CompleteEvalRunCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var run = await db.EvalRuns.FindAsync([command.Request.EvalRunId], ct);
        if (run is null)
            return Result.Failure<EvalRunDto>("EvalRun not found.");

        run.Complete(
            command.Request.PromptTokens,
            command.Request.CompletionTokens,
            command.Request.TurnCount,
            command.Request.CheckExitCode,
            command.Request.TranscriptRef,
            command.Request.CheckOutput);
        await db.SaveChangesAsync(ct);
        return Result.Ok(EvalRunMapper.Map(run));
    }
}
/// <summary>
/// Lists eval runs for a project, optionally for one task.
/// </summary>
public class ListEvalRunsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListEvalRunsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<EvalRunDto>>> HandleAsync(ListEvalRunsRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var query = db.EvalRuns
            .Where(e => e.ProjectId == request.ProjectId);

        if (request.TaskId is not null)
            query = query.Where(e => e.TaskId == request.TaskId);

        var items = await query
            .OrderBy(e => e.Condition)
            .ThenBy(e => e.StartedAt)
            .Select(e => new EvalRunDto(
                e.Id,
                e.ProjectId,
                e.TaskId,
                e.PairId,
                e.Condition.ToString(),
                e.PromptTokens,
                e.CompletionTokens,
                e.TurnCount,
                e.Passed,
                e.StartedAt,
                e.CompletedAt,
                e.TranscriptRef))
            .ToListAsync(ct);

        return Result.Ok((IList<EvalRunDto>)items);
    }
}

internal static class EvalRunMapper
{
    internal static EvalRunDto Map(EvalRun run) =>
        new(
            run.Id,
            run.ProjectId,
            run.TaskId,
            run.PairId,
            run.Condition.ToString(),
            run.PromptTokens,
            run.CompletionTokens,
            run.TurnCount,
            run.Passed,
            run.StartedAt,
            run.CompletedAt,
            run.TranscriptRef);
}
