namespace ProjectBeacon.Application.Reports;

using Application.Common;
using Application.Evals;
using Domain.Entities.Evals;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record GetContextCostReportRequest(Guid TaskId, Guid? ProjectId = null);

public record ContextCostPairDto(
    string PairId,
    EvalRunDto? WithBrief,
    EvalRunDto? WithoutBrief,
    int? PromptTokensDelta,
    int? CompletionTokensDelta,
    int? TotalTokensDelta,
    int? TurnCountDelta,
    bool? WithBriefPassed,
    bool? WithoutBriefPassed);

public record ContextCostReportDto(
    Guid TaskId,
    Guid ProjectId,
    string TaskTitle,
    int PairCount,
    IReadOnlyList<ContextCostPairDto> Pairs);

public class ContextCostReportHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ContextCostReportHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ContextCostReportDto>> HandleAsync(GetContextCostReportRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var taskQuery = db.Tasks.Where(t => t.Id == request.TaskId);
        if (request.ProjectId is Guid projectId)
            taskQuery = taskQuery.Where(t => t.ProjectId == projectId);

        var task = await taskQuery.FirstOrDefaultAsync(ct);
        if (task is null)
            return Result.Failure<ContextCostReportDto>("Task not found.");

        var runs = await db.EvalRuns
            .Where(e => e.ProjectId == task.ProjectId && e.TaskId == task.Id && e.PairId != null && e.PairId != "")
            .ToListAsync(ct);

        var pairs = runs
            .GroupBy(e => e.PairId!)
            .OrderByDescending(g => g.Max(e => e.StartedAt))
            .Select(g => CreatePair(g.Key, g.ToList()))
            .Where(p => p is not null)
            .Cast<ContextCostPairDto>()
            .ToList();

        return Result.Ok(new ContextCostReportDto(
            task.Id,
            task.ProjectId,
            task.Title,
            pairs.Count,
            pairs));
    }

    private static ContextCostPairDto? CreatePair(string pairId, IReadOnlyList<EvalRun> runs)
    {
        var withBrief = runs
            .Where(e => e.Condition == EvalCondition.WithBrief)
            .OrderBy(e => e.StartedAt)
            .ThenBy(e => e.Id)
            .LastOrDefault();

        var withoutBrief = runs
            .Where(e => e.Condition == EvalCondition.WithoutBrief)
            .OrderBy(e => e.StartedAt)
            .ThenBy(e => e.Id)
            .LastOrDefault();

        if (withBrief is null && withoutBrief is null)
            return null;

        var bothCompleted = withBrief?.CompletedAt is not null && withoutBrief?.CompletedAt is not null;
        int? promptDelta = bothCompleted
            ? withBrief!.PromptTokens - withoutBrief!.PromptTokens
            : null;
        int? completionDelta = bothCompleted
            ? withBrief!.CompletionTokens - withoutBrief!.CompletionTokens
            : null;
        int? totalDelta = promptDelta is not null && completionDelta is not null
            ? promptDelta + completionDelta
            : null;
        int? turnCountDelta = bothCompleted
            ? withBrief!.TurnCount - withoutBrief!.TurnCount
            : null;

        return new ContextCostPairDto(
            pairId,
            withBrief is null ? null : Map(withBrief),
            withoutBrief is null ? null : Map(withoutBrief),
            promptDelta,
            completionDelta,
            totalDelta,
            turnCountDelta,
            withBrief?.Passed,
            withoutBrief?.Passed);
    }

    private static EvalRunDto Map(EvalRun run) => new(
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
