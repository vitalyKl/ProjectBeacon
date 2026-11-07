namespace ProjectBeacon.Application.Evals;

using Application.Common;
using Application.Context;
using Application.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

/// <summary>
/// Pins the existing with/without-brief evaluation pair for the native workstation runtime.
/// </summary>
public record EvalPairRequest(
    Guid ProjectId,
    Guid TaskId,
    Guid DeviceId,
    Guid ActorId,
    string Prompt,
    string? Path = null,
    string? Model = null,
    int? BudgetTokens = null,
    string? CheckCommand = null,
    double? Temperature = null,
    string? ReasoningEffort = null,
    string? ToolPermissions = null,
    int? TimeoutSeconds = null,
    string? RepoRevision = null);

/// <summary>Command for creating paired eval runs.</summary>
public record EvalPairCommand(EvalPairRequest Request) : ICommand<Result<EvalPairResult>>;

/// <summary>
/// Identifiers for the two evaluation variants and their workstation commands.
/// </summary>
public record EvalPairResult(
    string PairId,
    Guid WithBriefRunId,
    Guid WithoutBriefRunId,
    Guid WithBriefCommandId,
    Guid WithoutBriefCommandId)
{
}

/// <summary>
/// Enqueues a with/without-brief pair on an online device.
/// All model, sampling, permission, repository, check, and timeout controls are pinned.
/// </summary>
public class EvalPairHandler : ICommandHandler<EvalPairCommand, Result<EvalPairResult>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly CompileBriefHandler _compileBrief;
    private readonly RecordEvalRunHandler _recordEvalRun;
    private readonly EnqueueCommandHandler _enqueueCommand;

    public EvalPairHandler(
        IBeaconDbFactory dbFactory,
        CompileBriefHandler compileBrief,
        RecordEvalRunHandler recordEvalRun,
        EnqueueCommandHandler enqueueCommand)
    {
        _dbFactory = dbFactory;
        _compileBrief = compileBrief;
        _recordEvalRun = recordEvalRun;
        _enqueueCommand = enqueueCommand;
    }

    public async Task<Result<EvalPairResult>> HandleAsync(EvalPairCommand command, CancellationToken ct = default)
    {
        var request = command.Request;

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return Result.Failure<EvalPairResult>("Prompt is required.");
        if (string.IsNullOrWhiteSpace(request.Model)
            || request.Temperature is null
            || string.IsNullOrWhiteSpace(request.ReasoningEffort)
            || string.IsNullOrWhiteSpace(request.ToolPermissions)
            || string.IsNullOrWhiteSpace(request.RepoRevision)
            || string.IsNullOrWhiteSpace(request.CheckCommand)
            || request.TimeoutSeconds is not int timeout || timeout is <= 0 or > 3600)
            return Result.Failure<EvalPairResult>("Eval controls must pin model, temperature, reasoning, tools, revision, check, and timeout.");

        string? taskTitle;
        await using (var db = _dbFactory.CreateDbContext())
        {
            taskTitle = await db.Tasks.IgnoreQueryFilters()
                .Where(t => t.Id == request.TaskId && t.ProjectId == request.ProjectId)
                .Select(t => t.Title)
                .FirstOrDefaultAsync(ct);

            if (taskTitle is null)
                return Result.Failure<EvalPairResult>("Task not found.");

            var isMember = await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == request.ActorId, ct);
            if (!isMember)
                return Result.Failure<EvalPairResult>("Project not found.");

            var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
            if (device is null || device.IsRevoked || device.UserId != request.ActorId)
                return Result.Failure<EvalPairResult>("Device not found.");

            if (!device.IsOnline(DateTime.UtcNow))
                return Result.Failure<EvalPairResult>("Device is not connected.", ErrorKind.Conflict);
        }

        var briefResult = await _compileBrief.HandleAsync(
            new CompileBriefCommand(new CompileBriefRequest(
                request.ProjectId, null, request.Path, request.TaskId,
                request.BudgetTokens, false, false, false)), ct);
        if (!briefResult.Success)
            return Result.Failure<EvalPairResult>(briefResult.Error ?? "Failed to compile brief.");

        var briefText = briefResult.Value!.BriefMarkdown;
        var trimmedPrompt = request.Prompt.Trim();
        var withBriefPrompt = string.IsNullOrWhiteSpace(briefText)
            ? trimmedPrompt
            : $"{briefText.Trim()}\n\n## Task request\n\n{trimmedPrompt}";
        var withoutBriefPrompt = trimmedPrompt;
        var pairId = Guid.NewGuid().ToString("n");

        var variants = new (EvalCondition Condition, string Runtime, string Prompt)[]
        {
            (EvalCondition.WithBrief, "native", withBriefPrompt),
            (EvalCondition.WithoutBrief, "native", withoutBriefPrompt)
        };

        var completed = new List<(EvalCondition Condition, string Runtime, Guid RunId, Guid CommandId)>(2);
        foreach (var variant in variants)
        {
            var recorded = await _recordEvalRun.HandleAsync(new RecordEvalRunCommand(
                new RecordEvalRunRequest(request.ProjectId, request.TaskId, pairId, variant.Condition)), ct);
            if (!recorded.Success)
                return Result.Failure<EvalPairResult>(recorded.Error ?? "Failed to record eval run.");

            await using (var pinDb = _dbFactory.CreateDbContext())
            {
                var run = await pinDb.EvalRuns.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(item => item.Id == recorded.Value!.Id, ct);
                if (run is null)
                    return Result.Failure<EvalPairResult>("The newly recorded eval run could not be reloaded.");
                run.Pin(
                    request.Model!.Trim(),
                    request.Temperature!.Value,
                    request.ReasoningEffort!.Trim(),
                    request.ToolPermissions!.Trim(),
                    request.TimeoutSeconds!.Value,
                    request.RepoRevision!.Trim(),
                    request.CheckCommand!.Trim(),
                    variant.Runtime);
                await pinDb.SaveChangesAsync(ct);
            }

            var enqueued = await _enqueueCommand.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                request.DeviceId,
                request.ActorId,
                WorkstationCommandKind.RunEvalTurn,
                BuildPayload(recorded.Value!.Id, request.TaskId, taskTitle!, variant.Prompt, variant.Runtime, request),
                request.ProjectId)), ct);
            if (!enqueued.Success)
                return Result.Failure<EvalPairResult>(enqueued.Error ?? "Failed to enqueue eval command.");

            completed.Add((variant.Condition, variant.Runtime, recorded.Value.Id, enqueued.Value!.Id));
        }

        return Result.Ok(new EvalPairResult(
            pairId,
            completed[0].RunId,
            completed[1].RunId,
            completed[0].CommandId,
            completed[1].CommandId));
    }

    private static string BuildPayload(Guid runId, Guid taskId, string taskTitle, string prompt, string runtime, EvalPairRequest request)
    {
        var payload = new JsonObject
        {
            ["evalRunId"] = runId.ToString("D"),
            ["taskId"] = taskId.ToString("D"),
            ["runtime"] = runtime,
            ["title"] = $"Beacon eval: {taskTitle}",
            ["prompt"] = prompt,
            ["model"] = request.Model,
            ["checkCommand"] = request.CheckCommand,
            ["controls"] = new JsonObject
            {
                ["model"] = request.Model,
                ["temperature"] = JsonValue.Create(request.Temperature),
                ["reasoningEffort"] = request.ReasoningEffort,
                ["toolPermissions"] = request.ToolPermissions,
                ["timeoutSeconds"] = request.TimeoutSeconds,
                ["repoRevision"] = request.RepoRevision,
                ["checkCommand"] = request.CheckCommand
            }
        };

        if (!string.IsNullOrWhiteSpace(request.Path))
            payload["path"] = request.Path;

        return payload.ToJsonString();
    }
}
