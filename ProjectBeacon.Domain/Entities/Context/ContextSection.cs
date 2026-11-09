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

    /// <summary>Updates only a NeedsReview draft; agent-authored content remains unreviewed.</summary>
    public void UpdateDraft(string title, string bodyMarkdown, string? sourcePath, string? updatedById)
    {
        Update(title, bodyMarkdown, "agent", updatedById);
        Source = ContextSource.Native;
        SourcePath = sourcePath;
        ReviewState = ContextReviewState.NeedsReview;
    }
}
