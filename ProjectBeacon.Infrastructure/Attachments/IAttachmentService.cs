namespace ProjectBeacon.Infrastructure.Attachments;

using ProjectBeacon.Application.Authorization;

public sealed record TaskAttachmentDto(
    Guid Id, Guid TaskId, string FileName, string ContentType,
    long Length, DateTime CreatedAt, string CreatedByType);

public interface IAttachmentService
{
    Task<TaskAttachmentDto?> UploadTaskAsync(
        Guid projectId, Guid taskId, ActorContext actor, Stream content,
        string fileName, string? contentType, long length, CancellationToken ct = default);

    Task<IReadOnlyList<TaskAttachmentDto>> ListTaskAsync(
        Guid projectId, Guid taskId, CancellationToken ct = default);

    Task<(TaskAttachmentDto Attachment, byte[] Content)?> ReadTaskAsync(
        Guid projectId, Guid taskId, Guid attachmentId, CancellationToken ct = default);
}
