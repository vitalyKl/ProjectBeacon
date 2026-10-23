namespace ProjectBeacon.Application.Chat;

using Application.Agents;
using Application.Common;
using Application.Devices;
using Application.Identity;
using Domain.Entities.Chat;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

/// <summary>
/// Opens a chat on the current project's online device. Fails without a project folder, a client, or an OpenCode session id.
/// </summary>
public class CreateChatSessionHandler : ICommandHandler<CreateChatSessionCommand, Result<ChatSessionDto>>
{
    public const string ProjectFolderRequired = "Attach a project folder on the dashboard first.";
    public const string ClientRequired = "Start beacon client to chat.";

    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly GetCommandHandler _getCommand;

    public CreateChatSessionHandler(
        IBeaconDbFactory dbFactory,
        EnqueueCommandHandler enqueue,
        GetCommandHandler getCommand)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
        _getCommand = getCommand;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(CreateChatSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!db.FilterProjectId.HasValue || db.FilterProjectId == Guid.Empty)
            return Result.Failure<ChatSessionDto>("Project scope is not resolved.");
        var projectId = db.FilterProjectId.Value;
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure<ChatSessionDto>("Project not found.");

        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (runtime is null)
            return Result.Failure<ChatSessionDto>(ProjectFolderRequired);

        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == runtime.DeviceId, ct);
        if (device is null || !device.IsOnline(DateTime.UtcNow))
            return Result.Failure<ChatSessionDto>(ClientRequired);

        var resolved = await ProjectRuntimeResolver.ResolveAsync(db, projectId, device.Id, ct);
        if (!resolved.Success)
            return Result.Failure<ChatSessionDto>(resolved.Error ?? ProjectFolderRequired);

        var session = ChatSession.Create(projectId, device.Id, command.Request.Title ?? "Chat", resolved.Value!.LocalRoot);

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            device.Id, command.Request.UserId, WorkstationCommandKind.ChatEnsureSession,
            JsonSerializer.Serialize(new { title = session.Title }), projectId)), ct);
        if (!queued.Success)
            return Result.Failure<ChatSessionDto>(queued.Error ?? "Could not reach the device.");

        var finished = await WaitAsync(queued.Value!.Id, command.Request.UserId, ct);
        if (finished is null || finished.Status != WorkstationCommandStatus.Succeeded || string.IsNullOrEmpty(finished.ResultJson))
            return Result.Failure<ChatSessionDto>(finished?.Error ?? "OpenCode did not create a session.");

        var externalId = ReadSessionId(finished.ResultJson);
        if (string.IsNullOrWhiteSpace(externalId))
            return Result.Failure<ChatSessionDto>("OpenCode session id missing.");

        // Persist only after the device created the session, so failed attempts leave no orphan row.
        session.SetExternalSessionId(externalId);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(session));
    }

    private async Task<WorkstationCommandDto?> WaitAsync(Guid commandId, Guid userId, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var got = await _getCommand.HandleAsync(new GetCommandCommand(new GetCommandRequest(commandId, userId)), ct);
            if (!got.Success)
                return null;
            if (got.Value!.Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
                return got.Value;
            await Task.Delay(500, ct);
        }
        return null;
    }

    private static string? ReadSessionId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("sessionId", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ChatSessionDto Map(ChatSession session, string? projectName = null) =>
        new(session.Id, session.ProjectId, projectName, session.DeviceId, session.ExternalSessionId, session.Title,
            session.Status, session.CreatedAt, session.UpdatedAt);
}
/// <summary>
/// Lists the user's chat sessions in the current project. Archived sessions are hidden.
/// </summary>
public class ListChatSessionsHandler : ICommandHandler<ListChatSessionsCommand, Result<IList<ChatSessionDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListChatSessionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ChatSessionDto>>> HandleAsync(ListChatSessionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var sessions = await db.ChatSessions
            .Where(s => s.ArchivedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        var name = sessions.Count > 0
            ? await db.Projects
                .Where(p => p.Id == sessions[0].ProjectId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct)
            : null;
        return Result.Ok((IList<ChatSessionDto>)sessions.Select(s => CreateChatSessionHandler.Map(s, name)).ToList());
    }
}
/// <summary>
/// Loads one chat session the user can see. Archived sessions are hidden.
/// </summary>
public class GetChatSessionHandler : ICommandHandler<GetChatSessionCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetChatSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public virtual async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        var name = await db.Projects
            .Where(p => p.Id == session.ProjectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);
        return Result.Ok(CreateChatSessionHandler.Map(session, name));
    }
}
/// <summary>
/// Lists parts of a chat session. Fails when the session is missing or archived.
/// </summary>
public class ListChatPartsHandler : ICommandHandler<ListChatPartsCommand, Result<IList<ChatPartDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListChatPartsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ChatPartDto>>> HandleAsync(ListChatPartsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var exists = await db.ChatSessions
            .AnyAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (!exists)
            return Result.Failure<IList<ChatPartDto>>("Chat session not found.");
        var parts = await db.ChatParts
            .Where(p => p.SessionId == command.Request.SessionId)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<ChatPartDto>)parts.Select(MapPart).ToList());
    }

    internal static ChatPartDto MapPart(ChatPart p) =>
        new(p.Id, p.SessionId, p.Role, p.Kind, p.Body, p.ExternalId, p.SortOrder, p.CreatedAt,
            p.Tool, p.ToolStatus, p.ToolInput, p.ToolResult, p.DurationMs);
}
/// <summary>
/// Queues a prompt on the device. The message is required and the OpenCode session must already exist.
/// </summary>
public class SendChatPromptHandler : ICommandHandler<SendChatPromptCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly ChatEventSignal _events;

    public SendChatPromptHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue, ChatEventSignal events)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
        _events = events;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(SendChatPromptCommand command, CancellationToken ct = default)
    {
        var text = command.Request.Text?.Trim() ?? "";
        if (text.Length == 0)
            return Result.Failure<ChatSessionDto>("Message is required.");

        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        if (string.IsNullOrWhiteSpace(session.ExternalSessionId))
            return Result.Failure<ChatSessionDto>("OpenCode session is not ready.");

        var next = await db.ChatParts.Where(p => p.SessionId == session.Id).MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        var part = ChatPart.Create(session.Id, session.ProjectId, "user", "text", text, next + 1);

        var payload = JsonSerializer.Serialize(new
        {
            chatSessionId = session.Id,
            externalSessionId = session.ExternalSessionId,
            text,
            model = await ChatModelSelection.ResolveAsync(db, command.Request.UserId, command.Request.Model, ct)
        });
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            session.DeviceId, command.Request.UserId, WorkstationCommandKind.ChatPrompt, payload, session.ProjectId)), ct);
        if (!queued.Success)
            return Result.Failure<ChatSessionDto>(queued.Error ?? "Could not reach the device.");

        // Enqueue won, so the prompt and the streaming state are persisted together.
        db.ChatParts.Add(part);
        session.SetStreaming();
        await db.SaveChangesAsync(ct);
        _events.Signal(session.Id);
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}
/// <summary>
/// Marks the chat aborted and asks the device to stop the turn.
/// </summary>
public class AbortChatHandler : ICommandHandler<AbortChatCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly ChatEventSignal _events;

    public AbortChatHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue, ChatEventSignal events)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
        _events = events;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(AbortChatCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        session.Abort();
        if (!string.IsNullOrWhiteSpace(session.ExternalSessionId))
        {
            var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                session.DeviceId, command.Request.UserId, WorkstationCommandKind.ChatAbort,
                JsonSerializer.Serialize(new { externalSessionId = session.ExternalSessionId }), session.ProjectId)), ct);
            if (!queued.Success)
                return Result.Failure<ChatSessionDto>(queued.Error ?? "Could not reach the device.");
        }
        await db.SaveChangesAsync(ct);
        _events.Signal(session.Id);
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}
/// <summary>
/// Renames a chat session.
/// </summary>
public class RenameChatSessionHandler : ICommandHandler<RenameChatSessionCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RenameChatSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ChatSessionDto>> HandleAsync(RenameChatSessionCommand command, CancellationToken ct = default)
    {
        var title = command.Request.Title?.Trim() ?? "";
        if (title.Length == 0)
            return Result.Failure<ChatSessionDto>("Title is required.");
        if (title.Length > 200)
            return Result.Failure<ChatSessionDto>("Title is too long.");

        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        session.SetTitle(title);
        await db.SaveChangesAsync(ct);
        var name = await db.Projects
            .Where(p => p.Id == session.ProjectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);
        return Result.Ok(CreateChatSessionHandler.Map(session, name));
    }
}
/// <summary>
/// Archives (soft-deletes) a chat session. A streaming session is aborted on the device first.
/// </summary>
public class ArchiveChatSessionHandler : ICommandHandler<ArchiveChatSessionCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly AbortChatHandler _abort;
    private readonly ChatEventSignal _events;

    public ArchiveChatSessionHandler(IBeaconDbFactory dbFactory, AbortChatHandler abort, ChatEventSignal events)
    {
        _dbFactory = dbFactory;
        _abort = abort;
        _events = events;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(ArchiveChatSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        if (session.Status == ChatSessionStatus.Streaming)
        {
            var aborted = await _abort.HandleAsync(
                new AbortChatCommand(new AbortChatRequest(session.Id, command.Request.UserId)), ct);
            if (!aborted.Success)
                return Result.Failure<ChatSessionDto>(aborted.Error ?? "Could not reach the device.");
            // The abort handler committed in its own context; reload before archiving.
            await db.Entry(session).ReloadAsync(ct);
        }
        session.Archive();
        await db.SaveChangesAsync(ct);
        _events.Signal(session.Id);
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}
/// <summary>
/// Device path that appends a chat part and marks the session streaming. The session must belong to that device and not be archived. A repeated external id returns the existing part.
/// </summary>
public class AppendChatPartHandler : ICommandHandler<AppendChatPartCommand, Result<ChatPartDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ChatEventSignal _events;

    public AppendChatPartHandler(IBeaconDbFactory dbFactory, ChatEventSignal events)
    {
        _dbFactory = dbFactory;
        _events = events;
    }

    public async Task<Result<ChatPartDto>> HandleAsync(AppendChatPartCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null || session.DeviceId != command.Request.DeviceId)
            return Result.Failure<ChatPartDto>("Chat session not found.");
        if (!string.IsNullOrWhiteSpace(command.Request.ExternalId))
        {
            var existing = await db.ChatParts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.ExternalId == command.Request.ExternalId, ct);
            if (existing is not null)
                return Result.Ok(ListChatPartsHandler.MapPart(existing));
        }
        var next = await db.ChatParts.IgnoreQueryFilters()
            .Where(p => p.SessionId == session.Id).MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        var part = ChatPart.Create(session.Id, session.ProjectId, command.Request.Role, command.Request.Kind,
            command.Request.Body, next + 1, command.Request.ExternalId,
            command.Request.Tool, command.Request.ToolStatus, command.Request.ToolInput,
            command.Request.ToolResult, command.Request.DurationMs);
        db.ChatParts.Add(part);
        session.SetStreaming();
        await db.SaveChangesAsync(ct);
        _events.Signal(session.Id);
        return Result.Ok(ListChatPartsHandler.MapPart(part));
    }
}
/// <summary>
/// Device path that sets the chat back to idle.
/// </summary>
public class MarkChatIdleHandler : ICommandHandler<MarkChatIdleCommand, Result<ChatSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ChatEventSignal _events;

    public MarkChatIdleHandler(IBeaconDbFactory dbFactory, ChatEventSignal events)
    {
        _dbFactory = dbFactory;
        _events = events;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(MarkChatIdleCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.ArchivedAt == null, ct);
        if (session is null || session.DeviceId != command.Request.DeviceId)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        session.SetIdle();
        await db.SaveChangesAsync(ct);
        _events.Signal(session.Id);
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}
