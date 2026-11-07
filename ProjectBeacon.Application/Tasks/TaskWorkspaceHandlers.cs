namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;

public sealed record TaskWorkspaceSectionSummaryDto(
    Guid Id, string Key, string Title, string Abstract, string Kind,
    string AuthorType, string? AuthorId, string? AuthorRole, string? AuthorProfileKey,
    Guid? AuthorSubtaskId, Guid? AuthorSessionId, int Revision, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record TaskWorkspaceSectionDto(
    Guid Id, string Key, string Title, string Abstract, string Kind, string Content,
    string AuthorType, string? AuthorId, string? AuthorRole, string? AuthorProfileKey,
    Guid? AuthorSubtaskId, Guid? AuthorSessionId, int Revision, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record WorkspaceIndexRequest(Guid TaskId, string? Query = null, string? Kind = null, DateTime? Since = null, int Limit = 20);
public sealed record WorkspaceIndexQuery(WorkspaceIndexRequest Request) : IQuery<Result<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>>;
public sealed record WorkspaceReadRequest(Guid TaskId, string Key, int Offset = 0, int Limit = 8000);
public sealed record WorkspaceReadQuery(WorkspaceReadRequest Request) : IQuery<Result<TaskWorkspaceSectionDto>>;
public sealed record WorkspaceWriteRequest(
    Guid TaskId, string Key, string? Title, string? Abstract, string Content,
    string Mode = "append", int? ExpectedRevision = null);
public sealed record WorkspaceWriteCommand(
    WorkspaceWriteRequest Request, ActorContext Actor,
    string? PipelineRole = null, Guid? PipelineSubtaskId = null, Guid? PipelineSessionId = null)
    : ICommand<Result<TaskWorkspaceSectionDto>>;

public sealed class WorkspaceIndexHandler : IQueryHandler<WorkspaceIndexQuery, Result<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    public WorkspaceIndexHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>> HandleAsync(WorkspaceIndexQuery query, CancellationToken ct = default)
    {
        if (query.Request.TaskId == Guid.Empty)
            return Result.Failure<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>("TaskId is required.");
        var limit = Math.Clamp(query.Request.Limit, 1, 100);
        await using var db = _dbFactory.CreateDbContext();
        if (!await db.Tasks.AnyAsync(t => t.Id == query.Request.TaskId, ct))
            return Result.Failure<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>("Task not found.", ErrorKind.NotFound);

        var sections = db.TaskWorkspaceSections.AsNoTracking().Where(s => s.TaskId == query.Request.TaskId);
        if (!string.IsNullOrWhiteSpace(query.Request.Kind))
            sections = sections.Where(s => s.Kind == query.Request.Kind.Trim());
        if (query.Request.Since is { } since)
            sections = sections.Where(s => s.UpdatedAt >= since);
        if (!string.IsNullOrWhiteSpace(query.Request.Query))
        {
            var term = query.Request.Query.Trim().ToLowerInvariant();
            sections = sections.Where(s => s.Key.ToLower().Contains(term)
                || s.Title.ToLower().Contains(term)
                || s.Abstract.ToLower().Contains(term)
                || s.Content.ToLower().Contains(term));
        }

        var result = await sections.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Key)
            .Take(limit)
            .Select(s => new TaskWorkspaceSectionSummaryDto(
                s.Id, s.Key, s.Title, s.Abstract, s.Kind, s.AuthorType, s.AuthorId,
                s.AuthorRole, s.AuthorProfileKey, s.AuthorSubtaskId, s.AuthorSessionId,
                s.Revision, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);
        return Result<IReadOnlyList<TaskWorkspaceSectionSummaryDto>>.Ok(result);
    }
}

public sealed class WorkspaceReadHandler : IQueryHandler<WorkspaceReadQuery, Result<TaskWorkspaceSectionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    public WorkspaceReadHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskWorkspaceSectionDto>> HandleAsync(WorkspaceReadQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        if (request.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(request.Key))
            return Result.Failure<TaskWorkspaceSectionDto>("TaskId and section key are required.");
        if (request.Offset < 0 || request.Limit is < 1 or > 8000)
            return Result.Failure<TaskWorkspaceSectionDto>("Offset must be non-negative and limit must be between 1 and 8000.");

        await using var db = _dbFactory.CreateDbContext();
        var section = await db.TaskWorkspaceSections.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TaskId == request.TaskId && s.Key == request.Key, ct);
        if (section is null)
            return Result.Failure<TaskWorkspaceSectionDto>("Workspace section not found.", ErrorKind.NotFound);

        return Result.Ok(ToDto(section, section.Content.Substring(
            Math.Min(request.Offset, section.Content.Length),
            Math.Min(request.Limit, Math.Max(0, section.Content.Length - request.Offset)))));
    }

    internal static TaskWorkspaceSectionDto ToDto(TaskWorkspaceSection section, string? content = null) =>
        new(section.Id, section.Key, section.Title, section.Abstract, section.Kind, content ?? section.Content,
            section.AuthorType, section.AuthorId, section.AuthorRole, section.AuthorProfileKey,
            section.AuthorSubtaskId, section.AuthorSessionId, section.Revision, section.CreatedAt, section.UpdatedAt);
}

public sealed class WorkspaceWriteHandler : ICommandHandler<WorkspaceWriteCommand, Result<TaskWorkspaceSectionDto>>
{
    public const int MaxWriteLength = 8000;
    public const int MaxSectionLength = 20000;
    public const int MaxSectionsPerTask = 100;
    public const int MaxTaskLength = 500000;

    private readonly IBeaconDbFactory _dbFactory;
    public WorkspaceWriteHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskWorkspaceSectionDto>> HandleAsync(WorkspaceWriteCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var key = request.Key?.Trim() ?? string.Empty;
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? "append";
        var content = request.Content?.TrimEnd() ?? string.Empty;
        var title = request.Title?.Trim() ?? string.Empty;
        var abstractText = request.Abstract?.Trim() ?? string.Empty;

        if (request.TaskId == Guid.Empty || !ValidKey(key))
            return Result.Failure<TaskWorkspaceSectionDto>("A valid taskId and lowercase workspace key are required.");
        if (content.Length is 0 or > MaxWriteLength)
            return Result.Failure<TaskWorkspaceSectionDto>($"Content must contain 1 to {MaxWriteLength} characters.");
        if (mode is not ("create" or "append" or "replace"))
            return Result.Failure<TaskWorkspaceSectionDto>("Mode must be create, append, or replace.");
        if (mode is "create" or "replace")
        {
            if (title.Length is 0 or > 200 || abstractText.Length is 0 or > 200)
                return Result.Failure<TaskWorkspaceSectionDto>("Title and abstract are required and must not exceed 200 characters.");
        }

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        db.ChangeTracker.Clear();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskWorkspaceSectionDto>("Task not found.", ErrorKind.NotFound);

        var authorType = command.Actor.Type.ToString();
        var authorId = command.Actor.UserId?.ToString("D")
            ?? command.Actor.TokenId?.ToString("D")
            ?? command.Actor.DeviceId?.ToString("D");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var section = await db.TaskWorkspaceSections
            .FirstOrDefaultAsync(s => s.TaskId == request.TaskId && s.Key == key, ct);

        if (mode == "create")
        {
            if (section is not null)
                return Result.Failure<TaskWorkspaceSectionDto>("A workspace section with this key already exists.", ErrorKind.Conflict);
            var count = await db.TaskWorkspaceSections.CountAsync(s => s.TaskId == request.TaskId, ct);
            if (count >= MaxSectionsPerTask)
                return Result.Failure<TaskWorkspaceSectionDto>($"A task can have at most {MaxSectionsPerTask} workspace sections.");
            var total = await db.TaskWorkspaceSections.Where(s => s.TaskId == request.TaskId)
                .Select(s => (long)s.Content.Length).SumAsync(ct);
            if (total + content.Length > MaxTaskLength)
                return Result.Failure<TaskWorkspaceSectionDto>("The task workspace has reached its total size limit.");

            section = TaskWorkspaceSection.Create(request.TaskId, task.ProjectId, key, title, abstractText,
                "Notes", content, authorType, authorId, command.PipelineRole, null,
                command.PipelineSubtaskId, command.PipelineSessionId);
            db.TaskWorkspaceSections.Add(section);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(WorkspaceReadHandler.ToDto(section));
        }

        if (section is null)
            return Result.Failure<TaskWorkspaceSectionDto>("Workspace section not found.", ErrorKind.NotFound);
        if (section.Kind == "Report")
            return Result.Failure<TaskWorkspaceSectionDto>("Server-generated report sections are immutable.", ErrorKind.Forbidden);

        if (mode == "replace" && content.Length > MaxSectionLength)
            return Result.Failure<TaskWorkspaceSectionDto>($"A workspace section cannot exceed {MaxSectionLength} characters.");

        var existingTotal = await db.TaskWorkspaceSections
            .Where(s => s.TaskId == request.TaskId)
            .Select(s => (long)s.Content.Length)
            .SumAsync(ct);
        var projectedLength = mode == "append"
            ? (long)section.Content.Length + content.Length + 2
            : content.Length;
        if (existingTotal - section.Content.Length + projectedLength > MaxTaskLength)
            return Result.Failure<TaskWorkspaceSectionDto>("The task workspace has reached its total size limit.");

        var now = DateTime.UtcNow;
        int changed;
        if (mode == "append")
        {
            changed = await db.TaskWorkspaceSections
                .Where(s => s.Id == section.Id && s.Kind == "Notes"
                    && s.Content.Length <= MaxSectionLength - content.Length - 2)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(s => s.Content, s => s.Content + "\n\n" + content)
                    .SetProperty(s => s.Revision, s => s.Revision + 1)
                    .SetProperty(s => s.UpdatedAt, now), ct);
        }
        else
        {
            if (request.ExpectedRevision is null)
                return Result.Failure<TaskWorkspaceSectionDto>("expectedRevision is required for replace.");
            if (!string.Equals(section.AuthorType, authorType, StringComparison.Ordinal)
                || !string.Equals(section.AuthorId, authorId, StringComparison.Ordinal))
                return Result.Failure<TaskWorkspaceSectionDto>("Only the section author may replace it.", ErrorKind.Forbidden);
            changed = await db.TaskWorkspaceSections
                .Where(s => s.Id == section.Id && s.Revision == request.ExpectedRevision.Value
                    && s.AuthorType == authorType && s.AuthorId == authorId && s.Kind == "Notes")
                .ExecuteUpdateAsync(update => update
                    .SetProperty(s => s.Title, title)
                    .SetProperty(s => s.Abstract, abstractText)
                    .SetProperty(s => s.Content, content)
                    .SetProperty(s => s.Revision, s => s.Revision + 1)
                    .SetProperty(s => s.UpdatedAt, now), ct);
        }

        if (changed == 0)
            return Result.Failure<TaskWorkspaceSectionDto>(
                mode == "append" ? "Workspace section size limit reached or section changed; retry."
                    : "Workspace section revision changed; reload and retry.", ErrorKind.Conflict);

        await transaction.CommitAsync(ct);
        var updated = await db.TaskWorkspaceSections.AsNoTracking().FirstAsync(s => s.Id == section.Id, ct);
        return Result.Ok(WorkspaceReadHandler.ToDto(updated));
        });
    }

    private static bool ValidKey(string key) =>
        key.Length is > 0 and <= 160
        && !key.StartsWith('/') && !key.EndsWith('/')
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains("//", StringComparison.Ordinal)
        && key == key.ToLowerInvariant()
        && key.Split('/').All(segment => segment.Length > 0
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
}
