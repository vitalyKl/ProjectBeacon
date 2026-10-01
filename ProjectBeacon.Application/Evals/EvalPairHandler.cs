namespace ProjectBeacon.Application.Evals;

using Application.Common;
using Application.Context;
using Application.Devices;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

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

public record EvalPairCommand(EvalPairRequest Request) : ICommand<Result<EvalPairResult>>;

public record EvalPairResult(
    string PairId,
    Guid WithBriefRunId,
    Guid WithoutBriefRunId,
    Guid WithBriefCommandId,
    Guid WithoutBriefCommandId);

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
                return Result.Failure<EvalPairResult>("Device is not connected.");
        }

        var briefResult = await _compileBrief.HandleAsync(
            new CompileBriefCommand(new CompileBriefRequest(
                request.ProjectId, null, request.Path, request.TaskId,
                request.BudgetTokens, false, false, false)), ct);

        var briefText = briefResult.Success ? briefResult.Value!.BriefMarkdown : string.Empty;
        var trimmedPrompt = request.Prompt.Trim();

        var withBriefPrompt = string.IsNullOrWhiteSpace(briefText)
            ? trimmedPrompt
            : $"{briefText.Trim()}\n\n## Task request\n\n{trimmedPrompt}";

        var withoutBriefPrompt = trimmedPrompt;

        var pairId = Guid.NewGuid().ToString("n");

        var withBrief = await _recordEvalRun.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(request.ProjectId, request.TaskId, pairId, EvalCondition.WithBrief)), ct);
        if (!withBrief.Success)
            return Result.Failure<EvalPairResult>(withBrief.Error ?? "Failed to record eval run.");

        var withoutBrief = await _recordEvalRun.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(request.ProjectId, request.TaskId, pairId, EvalCondition.WithoutBrief)), ct);
        if (!withoutBrief.Success)
            return Result.Failure<EvalPairResult>(withoutBrief.Error ?? "Failed to record eval run.");

        var withBriefCommand = await _enqueueCommand.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            request.DeviceId, request.ActorId, WorkstationCommandKind.RunEvalTurn,
            BuildPayload(withBrief.Value!.Id, taskTitle!, withBriefPrompt, request), request.ProjectId)), ct);
        if (!withBriefCommand.Success)
            return Result.Failure<EvalPairResult>(withBriefCommand.Error ?? "Failed to enqueue command.");

        var withoutBriefCommand = await _enqueueCommand.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            request.DeviceId, request.ActorId, WorkstationCommandKind.RunEvalTurn,
            BuildPayload(withoutBrief.Value!.Id, taskTitle!, withoutBriefPrompt, request), request.ProjectId)), ct);
        if (!withoutBriefCommand.Success)
            return Result.Failure<EvalPairResult>(withoutBriefCommand.Error ?? "Failed to enqueue command.");

        return Result.Ok(new EvalPairResult(
            pairId,
            withBrief.Value.Id,
            withoutBrief.Value.Id,
            withBriefCommand.Value!.Id,
            withoutBriefCommand.Value!.Id));
    }

    private static string BuildPayload(Guid runId, string taskTitle, string prompt, EvalPairRequest request)
    {
        var payload = new JsonObject
        {
            ["evalRunId"] = runId.ToString("D"),
            ["title"] = $"Beacon eval: {taskTitle}",
            ["prompt"] = prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Path))
            payload["path"] = request.Path;
        if (!string.IsNullOrWhiteSpace(request.Model))
            payload["model"] = request.Model;
        if (!string.IsNullOrWhiteSpace(request.CheckCommand))
            payload["checkCommand"] = request.CheckCommand;

        var timeout = request.TimeoutSeconds is > 0 and <= 3600 ? request.TimeoutSeconds.Value : 180;
        payload["controls"] = new JsonObject
        {
            ["model"] = request.Model ?? "",
            ["temperature"] = request.Temperature is double temperature ? JsonValue.Create(temperature) : null,
            ["reasoningEffort"] = request.ReasoningEffort ?? "",
            ["toolPermissions"] = request.ToolPermissions ?? "",
            ["timeoutSeconds"] = timeout,
            ["repoRevision"] = request.RepoRevision ?? "",
            ["checkCommand"] = request.CheckCommand ?? ""
        };

        return payload.ToJsonString();
    }
}
