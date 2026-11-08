namespace ProjectBeacon.Infrastructure.Attachments;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Tasks;

public sealed class DatabaseAttachmentService : IAttachmentService
{
    public const long MaxFileLength = 10 * 1024 * 1024;

    private readonly IBeaconDbFactory _dbFactory;
    private readonly string? _legacyRoot;

    public DatabaseAttachmentService(IBeaconDbFactory dbFactory)
    {
        _dbFactory = dbFactory;

        // Read-only compatibility path for files created by earlier versions.
        // New uploads are always stored in PostgreSQL; construction performs no filesystem I/O.
        var configured = Environment.GetEnvironmentVariable("BEACON_ATTACHMENT_ROOT");
        _legacyRoot = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    public async Task<TaskAttachmentDto?> UploadTaskAsync(
        Guid projectId, Guid taskId, ActorContext actor, Stream content,
        string fileName, string? contentType, long length, CancellationToken ct = default)
    {
        if (length is <= 0 or > MaxFileLength)
            throw new ArgumentOutOfRangeException(nameof(length), $"Files must be between 1 byte and {MaxFileLength} bytes.");

        var safeName = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        safeName = new string(safeName.Where(ch => !char.IsControl(ch)).Take(255).ToArray());
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new ArgumentException("A valid file name is required.", nameof(fileName));

        var normalizedType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim().Split(';', 2)[0].ToLowerInvariant();
        if (normalizedType.Length > 160)
            normalizedType = "application/octet-stream";

        await using var db = _dbFactory.CreateDbContext();
        var taskExists = await db.Tasks.AsNoTracking()
            .AnyAsync(item => item.Id == taskId && item.ProjectId == projectId, ct);
        if (!taskExists)
            return null;

        // The endpoint enforces this limit before opening the stream. Keep the same
        // validation here for every other caller, including workstation/MCP uploads.
        await using var buffer = new MemoryStream(checked((int)length));
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length != length)
            throw new InvalidDataException("Uploaded file length did not match the declared length.");

        var attachment = TaskAttachment.Create(
            projectId,
            taskId,
            safeName,
            normalizedType,
            length,
            Guid.NewGuid().ToString("N"),
            actor.Type.ToString(),
            actor.UserId ?? actor.TokenId ?? actor.DeviceId);
        var payload = TaskAttachmentContent.Create(projectId, attachment.Id, buffer.ToArray());

        // One SaveChanges call persists metadata and bytes in the same database transaction.
        db.TaskAttachments.Add(attachment);
        db.TaskAttachmentContents.Add(payload);
        await db.SaveChangesAsync(ct);
        return Map(attachment);
    }

    public async Task<IReadOnlyList<TaskAttachmentDto>> ListTaskAsync(
        Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var taskExists = await db.Tasks.AsNoTracking()
            .AnyAsync(item => item.Id == taskId && item.ProjectId == projectId, ct);
        if (!taskExists)
            return [];

        // Project only metadata. The bytea column is not selected for task lists.
        return await db.TaskAttachments.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.TaskId == taskId)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new TaskAttachmentDto(
                item.Id,
                item.TaskId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.CreatedAt,
                item.CreatedByType))
            .ToListAsync(ct);
    }

    public async Task<(TaskAttachmentDto Attachment, byte[] Content)?> ReadTaskAsync(
        Guid projectId, Guid taskId, Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var item = await db.TaskAttachments.AsNoTracking().SingleOrDefaultAsync(
            attachment => attachment.Id == attachmentId
                && attachment.ProjectId == projectId && attachment.TaskId == taskId, ct);
        if (item is null)
            return null;

        var bytes = await db.TaskAttachmentContents.AsNoTracking()
            .Where(payload => payload.ProjectId == projectId && payload.TaskAttachmentId == attachmentId)
            .Select(payload => payload.Content)
            .SingleOrDefaultAsync(ct);

        // Existing filesystem-backed attachments remain downloadable when their old
        // persistent directory is explicitly mounted. This path is read-only.
        if (bytes is null && _legacyRoot is not null)
        {
            var legacyPath = ResolveLegacyPath(item.StorageKey);
            if (File.Exists(legacyPath))
                bytes = await File.ReadAllBytesAsync(legacyPath, ct);
        }

        return bytes is null ? null : (Map(item), bytes);
    }

    private string ResolveLegacyPath(string key)
    {
        if (key.Length != 32 || !key.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid legacy attachment storage key.");

        var path = Path.GetFullPath(Path.Combine(_legacyRoot!, key));
        var relative = Path.GetRelativePath(_legacyRoot!, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Legacy attachment path escaped the storage root.");
        return path;
    }

    private static TaskAttachmentDto Map(TaskAttachment item) =>
        new(item.Id, item.TaskId, item.FileName, item.ContentType, item.Length, item.CreatedAt, item.CreatedByType);
}
