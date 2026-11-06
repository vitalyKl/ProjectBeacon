namespace ProjectBeacon.Infrastructure.Attachments;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Tasks;

public sealed class PhysicalAttachmentService : IAttachmentService
{
    public const long MaxFileLength = 10 * 1024 * 1024;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly string _root;

    public PhysicalAttachmentService(IBeaconDbFactory dbFactory)
    {
        _dbFactory = dbFactory;
        var configured = Environment.GetEnvironmentVariable("BEACON_ATTACHMENT_ROOT");
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "attachments")
            : configured);
        Directory.CreateDirectory(_root);
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
        var task = await db.Tasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == taskId && item.ProjectId == projectId, ct);
        if (task is null)
            return null;

        var storageKey = Guid.NewGuid().ToString("N");
        var path = ResolvePath(storageKey);
        try
        {
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(target, ct);
                if (target.Length != length)
                    throw new InvalidDataException("Uploaded file length did not match the declared length.");
            }

            var item = TaskAttachment.Create(projectId, taskId, safeName, normalizedType,
                length, storageKey, actor.Type.ToString(), actor.UserId ?? actor.TokenId ?? actor.DeviceId);
            db.TaskAttachments.Add(item);
            await db.SaveChangesAsync(ct);
            return Map(item);
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            throw;
        }
    }

    public async Task<IReadOnlyList<TaskAttachmentDto>> ListTaskAsync(
        Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var taskExists = await db.Tasks.AsNoTracking().AnyAsync(item => item.Id == taskId && item.ProjectId == projectId, ct);
        if (!taskExists)
            return [];
        var items = await db.TaskAttachments.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.TaskId == taskId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(ct);
        return items.Select(Map).ToArray();
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
        var path = ResolvePath(item.StorageKey);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return (Map(item), bytes);
    }

    private string ResolvePath(string key)
    {
        if (key.Length != 32 || !key.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid attachment storage key.");
        var path = Path.GetFullPath(Path.Combine(_root, key));
        var relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Attachment path escaped the storage root.");
        return path;
    }

    private static TaskAttachmentDto Map(TaskAttachment item) =>
        new(item.Id, item.TaskId, item.FileName, item.ContentType, item.Length, item.CreatedAt, item.CreatedByType);
}
