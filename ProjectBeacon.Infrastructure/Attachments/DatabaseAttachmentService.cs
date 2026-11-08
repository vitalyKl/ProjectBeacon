namespace ProjectBeacon.Infrastructure.Attachments;

using System.Text;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Tasks;

public sealed class DatabaseAttachmentService : IAttachmentService
{
    public const long MaxFileLength = 10 * 1024 * 1024;
    public const long DefaultProjectQuotaBytes = 1024L * 1024 * 1024;
    public const int DefaultMaxAttachmentsPerTask = 50;
    public const int DefaultMaxAttachmentsPerProject = 10_000;
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);

    private readonly IBeaconDbFactory _dbFactory;
    private readonly string? _legacyRoot;
    private readonly long _projectQuotaBytes;
    private readonly int _maxAttachmentsPerTask;
    private readonly int _maxAttachmentsPerProject;

    public DatabaseAttachmentService(IBeaconDbFactory dbFactory)
        : this(
            dbFactory,
            ReadPositiveLong("ATTACHMENT_PROJECT_QUOTA_BYTES", DefaultProjectQuotaBytes),
            ReadPositiveInt("ATTACHMENT_MAX_FILES_PER_TASK", DefaultMaxAttachmentsPerTask),
            ReadPositiveInt("ATTACHMENT_MAX_FILES_PER_PROJECT", DefaultMaxAttachmentsPerProject))
    {
    }

    public DatabaseAttachmentService(IBeaconDbFactory dbFactory, long projectQuotaBytes, int maxAttachmentsPerTask)
        : this(dbFactory, projectQuotaBytes, maxAttachmentsPerTask, DefaultMaxAttachmentsPerProject)
    {
    }

    public DatabaseAttachmentService(
        IBeaconDbFactory dbFactory, long projectQuotaBytes, int maxAttachmentsPerTask, int maxAttachmentsPerProject)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        if (projectQuotaBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(projectQuotaBytes));
        if (maxAttachmentsPerTask <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttachmentsPerTask));
        if (maxAttachmentsPerProject <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttachmentsPerProject));

        _dbFactory = dbFactory;
        _projectQuotaBytes = projectQuotaBytes;
        _maxAttachmentsPerTask = maxAttachmentsPerTask;
        _maxAttachmentsPerProject = maxAttachmentsPerProject;

        // Read-only compatibility path for files created by earlier versions.
        // New uploads are always stored in PostgreSQL; construction performs no filesystem I/O.
        var configured = Environment.GetEnvironmentVariable("BEACON_ATTACHMENT_ROOT");
        _legacyRoot = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    public async Task<TaskAttachmentDto?> UploadTaskAsync(
        Guid projectId, Guid taskId, ActorContext actor, Stream content,
        string fileName, string? contentType, long? length, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (length is <= 0 or > MaxFileLength)
            throw new AttachmentRejectedException($"Files must be between 1 byte and {MaxFileLength} bytes.");

        if (!await UploadSlots.WaitAsync(0, ct))
            throw new AttachmentConcurrencyLimitException("The server is processing the maximum number of simultaneous uploads.");

        try
        {
            var safeName = SanitizeFileName(fileName);
            var bytes = await ReadBoundedAsync(content, length, ct);
            // The request's Content-Type is untrusted. Persist only the type derived from the payload.
            var detectedType = AttachmentContentInspector.DetectContentType(safeName, bytes);
            _ = contentType;

            await using var strategyContext = _dbFactory.CreateDbContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // Each retry gets a fresh context because a failed transaction may have
                // changed EF's tracked entity states.
                await using var db = _dbFactory.CreateDbContext();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    // All attachment uploads for this project serialize on this transaction-
                    // scoped lock. Quotas are computed from rows, so cascaded deletes free
                    // space automatically and there is no denormalized counter to maintain.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({projectId.ToString("D")}, 0))", ct);
                }

                var taskExists = await db.Tasks.AsNoTracking()
                    .AnyAsync(item => item.Id == taskId && item.ProjectId == projectId, ct);
                if (!taskExists)
                    return null;

                var projectAttachmentCount = await db.TaskAttachments.AsNoTracking()
                    .CountAsync(item => item.ProjectId == projectId, ct);
                if (projectAttachmentCount >= _maxAttachmentsPerProject)
                    throw new AttachmentQuotaExceededException(
                        $"A project can have at most {_maxAttachmentsPerProject} attachments.");

                var taskAttachmentCount = await db.TaskAttachments.AsNoTracking()
                    .CountAsync(item => item.ProjectId == projectId && item.TaskId == taskId, ct);
                if (taskAttachmentCount >= _maxAttachmentsPerTask)
                    throw new AttachmentQuotaExceededException(
                        $"A task can have at most {_maxAttachmentsPerTask} attachments.");

                var projectBytes = await db.TaskAttachments.AsNoTracking()
                    .Where(item => item.ProjectId == projectId)
                    .SumAsync(item => (long?)item.Length, ct) ?? 0;
                if (projectBytes > _projectQuotaBytes - bytes.LongLength)
                    throw new AttachmentQuotaExceededException(
                        $"The project attachment quota of {_projectQuotaBytes} bytes would be exceeded.");

                var attachment = TaskAttachment.Create(
                    projectId,
                    taskId,
                    safeName,
                    detectedType,
                    bytes.LongLength,
                    Guid.NewGuid().ToString("N"),
                    actor.Type.ToString(),
                    actor.UserId ?? actor.TokenId ?? actor.DeviceId);
                var payload = TaskAttachmentContent.Create(projectId, attachment.Id, bytes);

                db.TaskAttachments.Add(attachment);
                db.TaskAttachmentContents.Add(payload);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Map(attachment);
            });
        }
        finally
        {
            UploadSlots.Release();
        }
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
        Guid projectId, Guid taskId, Guid attachmentId, CancellationToken ct = default, long? maxBytes = null)
    {
        var readLimit = Math.Min(maxBytes ?? MaxFileLength, MaxFileLength);
        if (readLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The attachment read limit must be positive.");

        await using var db = _dbFactory.CreateDbContext();
        var item = await db.TaskAttachments.AsNoTracking().SingleOrDefaultAsync(
            attachment => attachment.Id == attachmentId
                && attachment.ProjectId == projectId && attachment.TaskId == taskId, ct);
        if (item is null)
            return null;

        // Check metadata before selecting bytea. Agent reads can use a smaller cap than browser downloads.
        if (item.Length > readLimit)
            throw new AttachmentReadLimitExceededException(
                $"The attachment is {item.Length} bytes; the requested read limit is {readLimit} bytes.");

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
            {
                var info = new FileInfo(legacyPath);
                if (info.Length > readLimit)
                    throw new AttachmentReadLimitExceededException(
                        $"The legacy attachment exceeds the requested read limit of {readLimit} bytes.");
                bytes = await File.ReadAllBytesAsync(legacyPath, ct);
            }
        }

        if (bytes is null)
            return null;
        if (bytes.LongLength > readLimit)
            throw new AttachmentReadLimitExceededException(
                $"The attachment exceeds the requested read limit of {readLimit} bytes.");

        // Re-inspect legacy rows too: old metadata may contain a client-supplied MIME type.
        var detectedType = AttachmentContentInspector.DetectContentType(item.FileName, bytes);
        return (Map(item, detectedType), bytes);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, long? declaredLength, CancellationToken ct)
    {
        // Read directly into the final byte[] needed by PostgreSQL bytea. This avoids
        // the MemoryStream + ToArray double allocation and never trusts Content-Length.
        var capacity = declaredLength.HasValue
            ? checked((int)declaredLength.Value)
            : checked((int)MaxFileLength + 1);
        var bytes = new byte[capacity];
        var totalRead = 0;

        while (totalRead < capacity)
        {
            var read = await content.ReadAsync(bytes.AsMemory(totalRead, capacity - totalRead), ct);
            if (read == 0)
                break;
            totalRead += read;
        }

        if (declaredLength is null)
        {
            if (totalRead > MaxFileLength)
                throw new AttachmentRejectedException($"Files cannot exceed {MaxFileLength} bytes.");
        }
        else
        {
            if (totalRead != declaredLength.Value)
                throw new AttachmentRejectedException("Uploaded file length did not match the declared length.");

            var overflow = new byte[1];
            if (await content.ReadAsync(overflow.AsMemory(), ct) != 0)
                throw new AttachmentRejectedException("Uploaded file exceeded the declared length.");
        }

        if (totalRead == 0)
            throw new AttachmentRejectedException("Empty files are not accepted.");

        if (totalRead != bytes.Length)
            Array.Resize(ref bytes, totalRead);

        return bytes;
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new AttachmentRejectedException("A valid file name is required.");

        var candidate = Path.GetFileName(fileName.Replace('\\', '/')).Normalize(NormalizationForm.FormC);
        var safe = new string(candidate
            .Select(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) ? '_' : ch)
            .Take(255)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            throw new AttachmentRejectedException("A valid file name is required.");

        return safe;
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

    private static long ReadPositiveLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int ReadPositiveInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static TaskAttachmentDto Map(TaskAttachment item) =>
        Map(item, item.ContentType);

    private static TaskAttachmentDto Map(TaskAttachment item, string contentType) =>
        new(item.Id, item.TaskId, item.FileName, contentType, item.Length, item.CreatedAt, item.CreatedByType);
}

internal static class AttachmentContentInspector
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz", ".html", ".htm", ".xhtml", ".mhtml",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz",
        ".exe", ".dll", ".so", ".dylib", ".msi", ".com", ".scr", ".apk", ".dmg", ".pkg",
        ".jar", ".class"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".sql",
        ".css", ".scss", ".csproj", ".sln", ".props", ".targets", ".razor",
        ".sh", ".ps1", ".bat", ".cmd", ".ini", ".toml"
    };

    public static string DetectContentType(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (BlockedExtensions.Contains(extension) || IsExecutable(content) || IsArchive(content))
            throw new AttachmentRejectedException("This file type is not accepted.");

        var imageType = DetectImageType(content);
        if (imageType is not null)
        {
            var matchingExtension = imageType switch
            {
                "image/png" => extension is "" or ".png",
                "image/jpeg" => extension is "" or ".jpg" or ".jpeg",
                "image/gif" => extension is "" or ".gif",
                "image/webp" => extension is "" or ".webp",
                _ => false
            };
            if (!matchingExtension)
                throw new AttachmentRejectedException("The file extension does not match its image content.");
            return imageType;
        }

        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
            throw new AttachmentRejectedException("The image signature does not match the file extension.");

        if (content.AsSpan().StartsWith("%PDF-"u8))
        {
            if (extension is not ("" or ".pdf"))
                throw new AttachmentRejectedException("The file extension does not match its PDF content.");
            return "application/pdf";
        }

        if (extension == ".pdf")
            throw new AttachmentRejectedException("The PDF signature is missing.");

        if (TextExtensions.Contains(extension))
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetCharCount(content);
            }
            catch (DecoderFallbackException)
            {
                throw new AttachmentRejectedException("Text and source attachments must use valid UTF-8 encoding.");
            }

            if (content.AsSpan().Contains((byte)0))
                throw new AttachmentRejectedException("Text and source attachments cannot contain NUL bytes.");

            return "text/plain; charset=utf-8";
        }

        return "application/octet-stream";
    }

    private static string? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 6
            && (Encoding.ASCII.GetString(bytes[..6]) is "GIF87a" or "GIF89a"))
            return "image/gif";

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    private static bool IsExecutable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            return false;

        if (bytes[0] == 0x4D && bytes[1] == 0x5A) // DOS/PE
            return true;

        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x7F && bytes[1] == 0x45 && bytes[2] == 0x4C && bytes[3] == 0x46) // ELF
                return true;

            if ((bytes[0] == 0xFE && bytes[1] == 0xED && bytes[2] == 0xFA && bytes[3] is 0xCE or 0xCF)
                || (bytes[0] == 0xCE && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] is 0xFE or 0xCF)
                || (bytes[0] == 0xCF && bytes[1] == 0xFA && bytes[2] == 0xED && bytes[3] == 0xFE))
                return true;

            if (bytes[0] == 0xCA && bytes[1] == 0xFE && bytes[2] == 0xBA && bytes[3] == 0xBE)
                return true; // Java class / universal binary
        }

        return false;
    }

    private static bool IsArchive(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4
            && bytes[0] == 0x50 && bytes[1] == 0x4B
            && (bytes[2] == 0x03 && bytes[3] == 0x04
                || bytes[2] == 0x05 && bytes[3] == 0x06
                || bytes[2] == 0x07 && bytes[3] == 0x08))
            return true;

        if (bytes.Length >= 7
            && bytes[0] == 0x52 && bytes[1] == 0x61 && bytes[2] == 0x72 && bytes[3] == 0x21
            && bytes[4] == 0x1A && bytes[5] == 0x07)
            return true;

        if (bytes.Length >= 6
            && bytes[0] == 0x37 && bytes[1] == 0x7A && bytes[2] == 0xBC
            && bytes[3] == 0xAF && bytes[4] == 0x27 && bytes[5] == 0x1C)
            return true;

        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            return true;

        if (bytes.Length >= 3 && bytes[0] == 0x42 && bytes[1] == 0x5A && bytes[2] == 0x68)
            return true;

        if (bytes.Length >= 6
            && bytes[0] == 0xFD && bytes[1] == 0x37 && bytes[2] == 0x7A
            && bytes[3] == 0x58 && bytes[4] == 0x5A && bytes[5] == 0x00)
            return true;

        return bytes.Length >= 262 && bytes.Slice(257, 5).SequenceEqual("ustar"u8);
    }
}

public sealed class AttachmentRejectedException(string message) : Exception(message) { }

public sealed class AttachmentQuotaExceededException(string message) : Exception(message) { }

public sealed class AttachmentConcurrencyLimitException(string message) : Exception(message) { }

public sealed class AttachmentReadLimitExceededException(string message) : Exception(message) { }
