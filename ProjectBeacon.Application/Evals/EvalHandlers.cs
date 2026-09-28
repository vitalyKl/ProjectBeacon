namespace ProjectBeacon.Application.Evals;

using Common;
using Domain.Entities.Evals;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record RecordEvalRunRequest(Guid ProjectId, Guid TaskId, string? PairId, EvalCondition Condition);

public record RecordEvalRunCommand(RecordEvalRunRequest Request) : ICommand<Result<EvalRunDto>>;

public record CompleteEvalRunRequest(Guid EvalRunId, int PromptTokens, int CompletionTokens, int TurnCount, bool Passed, string? TranscriptRef);

public record CompleteEvalRunCommand(CompleteEvalRunRequest Request) : ICommand<Result<EvalRunDto>>;

public record ListEvalRunsRequest(Guid ProjectId, Guid? TaskId = null);

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

public class RecordEvalRunHandler : ICommandHandler<RecordEvalRunCommand, Result<EvalRunDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RecordEvalRunHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<EvalRunDto>> HandleAsync(RecordEvalRunCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var run = EvalRun.Create(command.Request.ProjectId, command.Request.TaskId, command.Request.PairId, command.Request.Condition);
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Result.Ok(EvalRunMapper.Map(run));
    }
}

public class CompleteEvalRunHandler : ICommandHandler<CompleteEvalRunCommand, Result<EvalRunDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CompleteEvalRunHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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
            command.Request.Passed,
            command.Request.TranscriptRef);
        await db.SaveChangesAsync(ct);
        return Result.Ok(EvalRunMapper.Map(run));
    }
}

public class ListEvalRunsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListEvalRunsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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
