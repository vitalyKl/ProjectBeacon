namespace ProjectBeacon.Application.Chat;

using Application.Auth;
using Application.Common;
using Application.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

public class CreateChatSessionHandler : ICommandHandler<CreateChatSessionCommand, Result<ChatSessionDto>>
{
    public const string ProjectFolderRequired = "Attach a project folder on the dashboard first.";
    public const string ClientRequired = "Start beacon client to chat.";

    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly GetCommandHandler _getCommand;

    public CreateChatSessionHandler(
        IDbContextFactory<BeaconDbContext> dbFactory,
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

        var session = ChatSession.Create(projectId, device.Id, command.Request.Title ?? "Chat", runtime.LocalRoot);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            device.Id, command.Request.UserId, WorkstationCommandKind.ChatEnsureSession,
            JsonSerializer.Serialize(new { path = runtime.LocalRoot, title = session.Title }), projectId)), ct);
        if (!queued.Success)
            return Result.Failure<ChatSessionDto>(queued.Error ?? "Could not reach the device.");

        var finished = await WaitAsync(queued.Value!.Id, command.Request.UserId, ct);
        if (finished is null || finished.Status != WorkstationCommandStatus.Succeeded || string.IsNullOrEmpty(finished.ResultJson))
            return Result.Failure<ChatSessionDto>(finished?.Error ?? "OpenCode did not create a session.");

        var externalId = ReadSessionId(finished.ResultJson);
        if (string.IsNullOrWhiteSpace(externalId))
            return Result.Failure<ChatSessionDto>("OpenCode session id missing.");

        session.SetExternalSessionId(externalId);
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

    internal static ChatSessionDto Map(ChatSession session) =>
        new(session.Id, session.ProjectId, session.DeviceId, session.ExternalSessionId, session.Title,
            session.LocalRoot, session.Status, session.CreatedAt, session.UpdatedAt);
}

public class ListChatSessionsHandler : ICommandHandler<ListChatSessionsCommand, Result<IList<ChatSessionDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListChatSessionsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ChatSessionDto>>> HandleAsync(ListChatSessionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var sessions = await db.ChatSessions.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        return Result.Ok((IList<ChatSessionDto>)sessions.Select(CreateChatSessionHandler.Map).ToList());
    }
}

public class GetChatSessionHandler : ICommandHandler<GetChatSessionCommand, Result<ChatSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetChatSessionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        return session is null
            ? Result.Failure<ChatSessionDto>("Chat session not found.")
            : Result.Ok(CreateChatSessionHandler.Map(session));
    }
}

public class ListChatPartsHandler : ICommandHandler<ListChatPartsCommand, Result<IList<ChatPartDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListChatPartsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ChatPartDto>>> HandleAsync(ListChatPartsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var exists = await db.ChatSessions.AnyAsync(s => s.Id == command.Request.SessionId, ct);
        if (!exists)
            return Result.Failure<IList<ChatPartDto>>("Chat session not found.");
        var parts = await db.ChatParts
            .Where(p => p.SessionId == command.Request.SessionId)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<ChatPartDto>)parts.Select(p =>
            new ChatPartDto(p.Id, p.SessionId, p.Role, p.Kind, p.Body, p.ExternalId, p.SortOrder, p.CreatedAt)).ToList());
    }
}

public class SendChatPromptHandler : ICommandHandler<SendChatPromptCommand, Result<ChatSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public SendChatPromptHandler(IDbContextFactory<BeaconDbContext> dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(SendChatPromptCommand command, CancellationToken ct = default)
    {
        var text = command.Request.Text?.Trim() ?? "";
        if (text.Length == 0)
            return Result.Failure<ChatSessionDto>("Message is required.");

        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        if (string.IsNullOrWhiteSpace(session.ExternalSessionId))
            return Result.Failure<ChatSessionDto>("OpenCode session is not ready.");

        var next = await db.ChatParts.Where(p => p.SessionId == session.Id).MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        db.ChatParts.Add(ChatPart.Create(session.Id, session.ProjectId, "user", "text", text, next + 1));
        session.SetStreaming();
        await db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            path = session.LocalRoot,
            chatSessionId = session.Id,
            externalSessionId = session.ExternalSessionId,
            text,
            model = await ChatModelSelection.ResolveAsync(db, command.Request.UserId, command.Request.Model, ct)
        });
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            session.DeviceId, command.Request.UserId, WorkstationCommandKind.ChatPrompt, payload, session.ProjectId)), ct);
        if (!queued.Success)
            return Result.Failure<ChatSessionDto>(queued.Error ?? "Could not reach the device.");
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}

public class AbortChatHandler : ICommandHandler<AbortChatCommand, Result<ChatSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public AbortChatHandler(IDbContextFactory<BeaconDbContext> dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<ChatSessionDto>> HandleAsync(AbortChatCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        if (session is null)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        session.Abort();
        await db.SaveChangesAsync(ct);
        if (!string.IsNullOrWhiteSpace(session.ExternalSessionId))
        {
            await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                session.DeviceId, command.Request.UserId, WorkstationCommandKind.ChatAbort,
                JsonSerializer.Serialize(new { externalSessionId = session.ExternalSessionId }), session.ProjectId)), ct);
        }
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}

public class AppendChatPartHandler : ICommandHandler<AppendChatPartCommand, Result<ChatPartDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AppendChatPartHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ChatPartDto>> HandleAsync(AppendChatPartCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        if (session is null || session.DeviceId != command.Request.DeviceId)
            return Result.Failure<ChatPartDto>("Chat session not found.");
        if (!string.IsNullOrWhiteSpace(command.Request.ExternalId))
        {
            var existing = await db.ChatParts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.ExternalId == command.Request.ExternalId, ct);
            if (existing is not null)
                return Result.Ok(new ChatPartDto(existing.Id, existing.SessionId, existing.Role, existing.Kind,
                    existing.Body, existing.ExternalId, existing.SortOrder, existing.CreatedAt));
        }
        var next = await db.ChatParts.IgnoreQueryFilters()
            .Where(p => p.SessionId == session.Id).MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
        var part = ChatPart.Create(session.Id, session.ProjectId, command.Request.Role, command.Request.Kind,
            command.Request.Body, next + 1, command.Request.ExternalId);
        db.ChatParts.Add(part);
        session.SetStreaming();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ChatPartDto(part.Id, part.SessionId, part.Role, part.Kind, part.Body, part.ExternalId, part.SortOrder, part.CreatedAt));
    }
}

public class MarkChatIdleHandler : ICommandHandler<MarkChatIdleCommand, Result<ChatSessionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public MarkChatIdleHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ChatSessionDto>> HandleAsync(MarkChatIdleCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.ChatSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
        if (session is null || session.DeviceId != command.Request.DeviceId)
            return Result.Failure<ChatSessionDto>("Chat session not found.");
        session.SetIdle();
        await db.SaveChangesAsync(ct);
        return Result.Ok(CreateChatSessionHandler.Map(session));
    }
}
