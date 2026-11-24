namespace ProjectBeacon.Domain.Entities.Roadmap;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A project release groups milestones and selects the intended branch/version target.
/// It is independent of Milestone; publishing is a separate server-authorized operation.
/// </summary>
public class Release : Entity, IProjectScoped
{
    public Release() { }

    public int Revision { get; private set; } = 1;
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? TargetVersion { get; private set; }
    public string TargetBranch { get; private set; } = string.Empty;
    public ReleaseStatus Status { get; private set; } = ReleaseStatus.Planned;
    public int Order { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    public Project Project { get; private set; } = null!;
    public ICollection<Milestone> Milestones { get; private set; } = [];

    public static Release Create(
        string name,
        string? description,
        Guid projectId,
        string? targetVersion,
        string targetBranch,
        int order)
    {
        var release = Entity.New<Release>();
        release.Name = name;
        release.Description = description;
        release.ProjectId = projectId;
        release.TargetVersion = targetVersion;
        release.TargetBranch = targetBranch;
        release.Order = order;
        release.Status = ReleaseStatus.Planned;
        release.CreatedAt = DateTime.UtcNow;
        return release;
    }

    public void Update(
        string? name = null,
        string? description = null,
        string? targetVersion = null,
        string? targetBranch = null,
        int? order = null)
    {
        if (Status is ReleaseStatus.Candidate or ReleaseStatus.Published or ReleaseStatus.Cancelled)
            throw new InvalidOperationException($"A {Status} release is immutable and cannot be edited.");

        var changed = false;
        if (name is not null && !string.Equals(Name, name, StringComparison.Ordinal))
        {
            Name = name;
            changed = true;
        }
        if (description is not null && !string.Equals(Description, description, StringComparison.Ordinal))
        {
            Description = description;
            changed = true;
        }
        if (targetVersion is not null && !string.Equals(TargetVersion, targetVersion, StringComparison.Ordinal))
        {
            TargetVersion = targetVersion;
            changed = true;
        }
        if (targetBranch is not null && !string.Equals(TargetBranch, targetBranch, StringComparison.Ordinal))
        {
            TargetBranch = targetBranch;
            changed = true;
        }
        if (order is not null && Order != order.Value)
        {
            Order = order.Value;
            changed = true;
        }
        if (!changed)
            return;

        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Changes planning status. Published can only be set by the publication workflow.</summary>
    public void TransitionTo(ReleaseStatus target)
    {
        if (target == Status)
            return;

        if (target == ReleaseStatus.Candidate)
            throw new InvalidOperationException("Only the authorized ReleasePrepare workflow can create an immutable Candidate.");

        var allowed = Status switch
        {
            ReleaseStatus.Planned => target is ReleaseStatus.InProgress or ReleaseStatus.Cancelled,
            ReleaseStatus.InProgress => target == ReleaseStatus.Cancelled,
            ReleaseStatus.Candidate => target == ReleaseStatus.Cancelled,
            ReleaseStatus.Published or ReleaseStatus.Cancelled => false,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Release cannot transition from {Status} to {target}.");

        if (target == ReleaseStatus.Published)
            throw new InvalidOperationException("Only the authorized publication workflow can publish a release.");

        Status = target;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Publication workflow transition after the immutable candidate and tag are verified.</summary>
    public void MarkPublished(DateTime publishedAtUtc)
    {
        if (Status != ReleaseStatus.Candidate)
            throw new InvalidOperationException("Only a Candidate release can be published.");
        if (publishedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Publication time must be UTC.", nameof(publishedAtUtc));

        Status = ReleaseStatus.Published;
        PublishedAt = publishedAtUtc;
        Revision++;
        UpdatedAt = publishedAtUtc;
    }

    public void MarkContentDeleted()
    {
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }
}
