namespace ProjectBeacon.Application.Tasks;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Domain.Enums;
using Domain.Common;
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

        JsonElement answer;
        try
        {
            using var answerDocument = JsonDocument.Parse(request.AnswerJson);
            if (answerDocument.RootElement.ValueKind != JsonValueKind.Object)
                return Result.Failure<PipelineSessionDto>("Answer must be a JSON object.");
            answer = answerDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result.Failure<PipelineSessionDto>("Answer must be a JSON object.");
        }

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

        PipelineActivityV1 activity;
        try
        {
            activity = PipelineActivityV1.Parse(session.ActivityJson);
        }
        catch (JsonException)
        {
            return Result.Failure<PipelineSessionDto>("Pipeline activity data is corrupted.");
        }

        var externalSessionId = activity.ExternalSessionId;
        if (string.IsNullOrWhiteSpace(externalSessionId)
            || !Guid.TryParse(activity.DeviceId, out var deviceId))
            return Result.Failure<PipelineSessionDto>("The workstation session is not available.");

        var form = activity.Forms.FirstOrDefault(item =>
            string.Equals(item.Id, formId, StringComparison.Ordinal));
        if (form is null)
            return Result.Failure<PipelineSessionDto>("Question not found.");
        if (form.Answer is not null)
            return Result.Failure<PipelineSessionDto>("Question already answered.", ErrorKind.Conflict);

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == request.UserId && d.RevokedAt == null, ct);
        if (device is null)
            return Result.Failure<PipelineSessionDto>("Workstation not found.");

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            deviceId,
            request.UserId,
            WorkstationCommandKind.ChatReplyForm,
            JsonSerializer.Serialize(new { externalSessionId, formId, answerJson = answer.GetRawText() }),
            session.ProjectId)), ct);
        if (!queued.Success)
            return Result.Failure<PipelineSessionDto>(queued.Error ?? "Could not deliver the answer to the workstation.");

        var updatedActivity = activity with
        {
            Forms = activity.Forms
                .Select(existing => string.Equals(existing.Id, formId, StringComparison.Ordinal)
                    ? existing with { Answer = answer }
                    : existing)
                .ToArray(),
            State = "running",
            Activity = "Answer sent; agent is continuing",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        session.UpdateActivity(updatedActivity.ToJson(), waitingForUser: false);
        await db.SaveChangesAsync(ct);
        return Result.Ok(PipelineMappers.ToDto(session));
    }
}

public sealed record WaitingPipelineTaskDto(Guid TaskId, string TaskTitle, Guid SessionId, DateTime? LaunchedAt);

public sealed record ListWaitingPipelineTasksRequest(Guid UserId);

public sealed record ListWaitingPipelineTasksCommand(ListWaitingPipelineTasksRequest Request)
    : ICommand<Result<IReadOnlyList<WaitingPipelineTaskDto>>>;

/// <summary>Lists task pipeline sessions that currently need an answer from the signed-in user.</summary>
public sealed class ListWaitingPipelineTasksHandler
    : ICommandHandler<ListWaitingPipelineTasksCommand, Result<IReadOnlyList<WaitingPipelineTaskDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListWaitingPipelineTasksHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<WaitingPipelineTaskDto>>> HandleAsync(
        ListWaitingPipelineTasksCommand command,
        CancellationToken ct = default)
    {
        if (command.Request.UserId == Guid.Empty)
            return Result.Failure<IReadOnlyList<WaitingPipelineTaskDto>>("User id is required.");

        await using var db = _dbFactory.CreateDbContext();
        var rows = await (
            from session in db.PipelineSessions.IgnoreQueryFilters()
            join member in db.ProjectMembers.IgnoreQueryFilters()
                on session.ProjectId equals member.ProjectId
            join task in db.Tasks.IgnoreQueryFilters()
                on session.TaskId equals task.Id
            where member.UserId == command.Request.UserId
                && session.Status == SessionStatus.WaitingForUser
            orderby session.LaunchedAt descending
            select new WaitingPipelineTaskDto(task.Id, task.Title, session.Id, session.LaunchedAt))
            .Take(20)
            .ToListAsync(ct);

        return Result.Ok((IReadOnlyList<WaitingPipelineTaskDto>)rows);
    }
}
