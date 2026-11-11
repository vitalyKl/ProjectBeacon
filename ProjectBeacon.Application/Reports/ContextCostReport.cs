namespace ProjectBeacon.Application.Reports;

using Application.Common;
using Application.Evals;
using Application.Authorization;
using Domain.Entities.Evals;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Task whose with-brief and without-brief eval pairs should be compared.
/// </summary>
public record GetContextCostReportRequest(Guid TaskId, Guid? ProjectId, ActorContext Actor);
/// <summary>
/// One eval pair with input-context, generated-output, and turn deltas reported independently.
/// </summary>
public record ContextCostPairDto(
    string PairId,
    EvalRunDto? WithBrief,
    EvalRunDto? WithoutBrief,
    int? PromptTokensDelta,
    int? CompletionTokensDelta,
    int? TurnCountDelta,
    bool? WithBriefPassed,
    bool? WithoutBriefPassed);
/// <summary>
/// The pairs compared for one task.
/// </summary>
public record ContextCostReportDto(
    Guid TaskId,
    Guid ProjectId,
    string TaskTitle,
    int PairCount,
    IReadOnlyList<ContextCostPairDto> Pairs);
/// <summary>
/// Compares input-context and generated-output token usage independently, without adding output tokens to context usage.
/// </summary>
public class ContextCostReportHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ContextCostReportHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ContextCostReportDto>> HandleAsync(GetContextCostReportRequest request, CancellationToken ct = default)
    {
        if (!request.Actor.IsHuman || request.Actor.UserId is null)
            return Result.Forbidden<ContextCostReportDto>();

        await using var db = _dbFactory.CreateDbContext();

        var taskQuery = db.Tasks.Where(t => t.Id == request.TaskId);
        if (request.ProjectId is Guid projectId)
            taskQuery = taskQuery.Where(t => t.ProjectId == projectId);

        var task = await taskQuery.FirstOrDefaultAsync(ct);
        if (task is null)
            return Result.Failure<ContextCostReportDto>("Task not found.");

        var access = await _authorization.CanAsync(
            db, request.Actor, ResourceType.Task, AuthAction.Read,
            projectId: task.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<ContextCostReportDto>();

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
        int? turnCountDelta = bothCompleted
            ? withBrief!.TurnCount - withoutBrief!.TurnCount
            : null;

        return new ContextCostPairDto(
            pairId,
            withBrief is null ? null : Map(withBrief),
            withoutBrief is null ? null : Map(withoutBrief),
            promptDelta,
            completionDelta,
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
        run.TranscriptRef,
        run.Runtime);
}
