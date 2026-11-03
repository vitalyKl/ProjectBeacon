namespace ProjectBeacon.Application.Tasks;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public sealed record ReplyPipelineFormRequest(
    Guid TaskId,
    Guid SessionId,
    Guid UserId,
    string FormId,
    string AnswerJson);

public sealed record ReplyPipelineFormCommand(ReplyPipelineFormRequest Request)
    : ICommand<Result<PipelineSessionDto>>;

/// <summary>
/// Answers an interactive form raised by a task pipeline agent. The session and form are
/// resolved from the task-scoped activity snapshot, while delivery goes through the device's
/// existing control-command lane.
/// </summary>
public sealed class ReplyPipelineFormHandler : ICommandHandler<ReplyPipelineFormCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public ReplyPipelineFormHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(
        ReplyPipelineFormCommand command,
        CancellationToken ct = default)
    {
        var request = command.Request;
        var formId = request.FormId?.Trim() ?? string.Empty;
        if (request.TaskId == Guid.Empty || request.SessionId == Guid.Empty
            || request.UserId == Guid.Empty || formId.Length == 0)
            return Result.Failure<PipelineSessionDto>("Task, session, user, and form identifiers are required.");

        JsonNode answer;
        try
        {
            answer = JsonNode.Parse(request.AnswerJson);
        }
        catch (JsonException)
        {
            return Result.Failure<PipelineSessionDto>("Answer must be a JSON object.");
        }
        if (answer is not JsonObject)
            return Result.Failure<PipelineSessionDto>("Answer must be a JSON object.");

        await using var db = _dbFactory.CreateDbContext();
        var session = await db.PipelineSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.TaskId == request.TaskId, ct);
        if (session is null)
            return Result.Failure<PipelineSessionDto>("Pipeline session not found.");
        if (session.Status != SessionStatus.WaitingForUser)
            return Result.Failure<PipelineSessionDto>("This pipeline session is not waiting for an answer.", ErrorKind.Conflict);

        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == session.ProjectId && m.UserId == request.UserId, ct);
        if (!member)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        JsonObject activity;
        try
        {
            activity = JsonNode.Parse(session.ActivityJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return Result.Failure<PipelineSessionDto>("Pipeline activity data is corrupted.");
        }

        var externalSessionId = activity["externalSessionId"]?.GetValue<string>();
        var deviceIdText = activity["deviceId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(externalSessionId)
            || !Guid.TryParse(deviceIdText, out var deviceId))
            return Result.Failure<PipelineSessionDto>("The workstation session is not available.");

        if (activity["forms"] is not JsonArray forms)
            return Result.Failure<PipelineSessionDto>("No pending form was found.");
        var form = forms.OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(item["id"]?.GetValue<string>(), formId, StringComparison.Ordinal));
        if (form is null)
            return Result.Failure<PipelineSessionDto>("Question not found.");
        if (form["answer"] is not null)
            return Result.Failure<PipelineSessionDto>("Question already answered.", ErrorKind.Conflict);

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == request.UserId && !d.IsRevoked, ct);
        if (device is null)
            return Result.Failure<PipelineSessionDto>("Workstation not found.");

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            deviceId,
            request.UserId,
            WorkstationCommandKind.ChatReplyForm,
            JsonSerializer.Serialize(new { externalSessionId, formId, answerJson = answer.ToJsonString() }),
            session.ProjectId)), ct);
        if (!queued.Success)
            return Result.Failure<PipelineSessionDto>(queued.Error ?? "Could not deliver the answer to the workstation.");

        form["answer"] = answer.DeepClone();
        activity["forms"] = forms;
        activity["state"] = "running";
        activity["activity"] = "Answer sent; agent is continuing";
        activity["updatedAt"] = DateTimeOffset.UtcNow;
        session.UpdateActivity(activity.ToJsonString(), waitingForUser: false);
        await db.SaveChangesAsync(ct);
        return Result.Ok(PipelineMappers.ToDto(session));
    }
}
