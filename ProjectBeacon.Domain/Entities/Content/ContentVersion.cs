namespace ProjectBeacon.Domain.Entities.Content;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class ContentVersion : Entity, IProjectScoped
{
    public ContentVersion() { }

    public Guid ProjectId { get; private set; }
    public ContentTargetKind TargetKind { get; private set; }
    public Guid TargetId { get; private set; }
    public int Revision { get; private set; }
    public string ContentJson { get; private set; } = "{}";
    public string? ChangedByType { get; private set; }
    public string? ChangedById { get; private set; }
    public Guid? ProposalId { get; private set; }
    public DateTime At { get; private set; }

    public static ContentVersion Create(
        Guid projectId,
        ContentTargetKind targetKind,
        Guid targetId,
        int revision,
        string contentJson,
        string? changedByType = null,
        string? changedById = null,
        Guid? proposalId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentJson);
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));

        var version = Entity.New<ContentVersion>();
        version.ProjectId = projectId;
        version.TargetKind = targetKind;
        version.TargetId = targetId;
        version.Revision = revision;
        version.ContentJson = contentJson;
        version.ChangedByType = changedByType;
        version.ChangedById = changedById;
        version.ProposalId = proposalId;
        version.At = DateTime.UtcNow;
        return version;
    }
}
