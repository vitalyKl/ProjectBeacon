namespace ProjectBeacon.Domain.Entities.Context;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// One section of the living brief. Scope is project, repo, path, or task.
/// <see cref="Update"/> refreshes <see cref="SectionsText"/> from the title and body.
/// </summary>
public class ContextSection : Entity, IProjectScoped
{
    public ContextSection() { }

    public int Revision { get; private set; } = 1;
    public string SectionId { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string BodyMarkdown { get; private set; } = string.Empty;
    public int Ordinal { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? RepoId { get; private set; }
    public Guid? TaskId { get; private set; }
    public ContextScopeType ScopeType { get; private set; }
    public string Path { get; private set; } = string.Empty;
    public string SectionsText { get; private set; } = string.Empty;
    public ContextSource Source { get; private set; }
    public string? SourcePath { get; private set; }
    public ContextReviewState ReviewState { get; private set; }
    public string? UpdatedByType { get; private set; }
    public string? UpdatedById { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public Project Project { get; private set; } = null!;
    public Project? Repo { get; private set; }

    public static ContextSection Create(
        string sectionId,
        string title,
        string bodyMarkdown,
        Guid projectId,
        ContextScopeType scopeType,
        string? key = null,
        int ordinal = 0,
        Guid? repoId = null,
        Guid? taskId = null,
        string? path = null,
        ContextSource source = ContextSource.Native,
        string? sourcePath = null,
        string? updatedByType = null,
        string? updatedById = null)
    {
        var section = Entity.New<ContextSection>();
        section.SectionId = sectionId;
        section.Key = key ?? string.Empty;
        section.Title = title;
        section.BodyMarkdown = bodyMarkdown;
        section.Ordinal = ordinal;
        section.ProjectId = projectId;
        section.ScopeType = scopeType;
        section.RepoId = repoId;
        section.TaskId = taskId;
        section.Path = path ?? string.Empty;
        section.Source = source;
        section.SourcePath = sourcePath;
        section.ReviewState = ContextReviewState.NeedsReview;
        section.UpdatedByType = updatedByType;
        section.UpdatedById = updatedById;
        section.UpdatedAt = DateTime.UtcNow;
        section.SectionsText = $"{title} {bodyMarkdown}";
        return section;
    }

    /// <summary>Recreates a physically deleted section from its immutable history identity.</summary>
    public static ContextSection RestoreDeleted(
        Guid id,
        Guid projectId,
        string sectionId,
        string key,
        string title,
        string bodyMarkdown,
        int ordinal,
        ContextScopeType scopeType,
        Guid? repoId,
        Guid? taskId,
        string path,
        ContextSource source,
        string? sourcePath,
        ContextReviewState reviewState,
        int revision,
        string? updatedByType,
        string? updatedById)
    {
        if (id == Guid.Empty || projectId == Guid.Empty || revision < 1)
            throw new ArgumentException("A section id, project id and positive revision are required.");

        var section = Entity.New<ContextSection>();
        section.Id = id;
        section.ProjectId = projectId;
        section.SectionId = sectionId;
        section.Key = key;
        section.Title = title;
        section.BodyMarkdown = bodyMarkdown;
        section.Ordinal = ordinal;
        section.ScopeType = scopeType;
        section.RepoId = repoId;
        section.TaskId = taskId;
        section.Path = path;
        section.Source = source;
        section.SourcePath = sourcePath;
        section.ReviewState = reviewState;
        section.Revision = revision;
        section.UpdatedByType = updatedByType;
        section.UpdatedById = updatedById;
        section.UpdatedAt = DateTime.UtcNow;
        section.SectionsText = $"{title} {bodyMarkdown}";
        return section;
    }

    public void Update(string? title = null, string? bodyMarkdown = null, string? updatedByType = null, string? updatedById = null)
    {
        var contentChanged =
            (title is not null && !string.Equals(Title, title, StringComparison.Ordinal))
            || (bodyMarkdown is not null && !string.Equals(BodyMarkdown, bodyMarkdown, StringComparison.Ordinal));
        if (title is not null) Title = title;
        if (bodyMarkdown is not null) BodyMarkdown = bodyMarkdown;
        if (contentChanged)
        {
            Revision++;
            SectionsText = $"{Title} {BodyMarkdown}";
        }
        if (updatedByType is not null) UpdatedByType = updatedByType;
        if (updatedById is not null) UpdatedById = updatedById;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Restores content and review state from a historical snapshot as a new revision.</summary>
    public void RestoreFromHistory(
        string title,
        string bodyMarkdown,
        ContextSource source,
        string? sourcePath,
        ContextReviewState reviewState,
        string? updatedByType,
        string? updatedById)
    {
        var changed =
            !string.Equals(Title, title, StringComparison.Ordinal)
            || !string.Equals(BodyMarkdown, bodyMarkdown, StringComparison.Ordinal)
            || Source != source
            || !string.Equals(SourcePath, sourcePath, StringComparison.Ordinal)
            || ReviewState != reviewState;
        if (!changed)
            return;

        Title = title;
        BodyMarkdown = bodyMarkdown;
        Source = source;
        SourcePath = sourcePath;
        ReviewState = reviewState;
        UpdatedByType = updatedByType;
        UpdatedById = updatedById;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
        SectionsText = $"{title} {bodyMarkdown}";
    }

    /// <summary>Updates an imported Markdown file while preserving its source identity in content history.</summary>
    public void UpdateImportedFile(
        string title,
        string bodyMarkdown,
        ContextSource source,
        string sourcePath,
        string? updatedByType,
        string? updatedById)
    {
        if (source == ContextSource.Native)
            throw new ArgumentException("Imported Markdown must have a non-native source.", nameof(source));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A source path is required for imported Markdown.", nameof(sourcePath));

        var changed =
            !string.Equals(Title, title, StringComparison.Ordinal)
            || !string.Equals(BodyMarkdown, bodyMarkdown, StringComparison.Ordinal)
            || Source != source
            || !string.Equals(SourcePath, sourcePath, StringComparison.Ordinal);

        Title = title;
        BodyMarkdown = bodyMarkdown;
        Source = source;
        SourcePath = sourcePath;
        if (changed)
        {
            Revision++;
            SectionsText = $"{title} {bodyMarkdown}";
        }

        if (updatedByType is not null)
            UpdatedByType = updatedByType;
        if (updatedById is not null)
            UpdatedById = updatedById;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Marks a NeedsReview section as approved by a human reviewer.</summary>
    public void MarkReviewed(string reviewerUserId)
    {
        if (ReviewState != ContextReviewState.NeedsReview)
            throw new InvalidOperationException("Only a context draft awaiting review can be approved.");
        if (string.IsNullOrWhiteSpace(reviewerUserId))
            throw new ArgumentException("A reviewer identity is required.", nameof(reviewerUserId));

        ReviewState = ContextReviewState.Reviewed;
        UpdatedByType = "user";
        UpdatedById = reviewerUserId;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Delete() => Revision++;

    /// <summary>Updates only a NeedsReview draft; agent-authored content remains unreviewed.</summary>
    public void UpdateDraft(string title, string bodyMarkdown, string? sourcePath, string? updatedById)
    {
        var revisionBefore = Revision;
        var metadataChanged =
            Source != ContextSource.Native
            || !string.Equals(SourcePath, sourcePath, StringComparison.Ordinal)
            || ReviewState != ContextReviewState.NeedsReview;

        Update(title, bodyMarkdown, "agent", updatedById);
        Source = ContextSource.Native;
        SourcePath = sourcePath;
        ReviewState = ContextReviewState.NeedsReview;

        // Update() owns text revisions. Source identity is part of the immutable
        // ContentVersion snapshot too, so a metadata-only path change must append a
        // revision without double-incrementing when the text changed at the same time.
        if (metadataChanged && Revision == revisionBefore)
            Revision++;
    }
}
