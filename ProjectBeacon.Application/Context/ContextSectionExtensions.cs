namespace ProjectBeacon.Application.Context;

using Domain.Entities.Projects;
using Domain.Enums;

public static class ContextSectionExtensions
{
    public static ContextSectionDto MapToDto(this ContextSection section)
    {
        return new ContextSectionDto(
            section.Id,
            section.SectionId,
            section.Key,
            section.Title,
            section.BodyMarkdown,
            section.Ordinal,
            section.ProjectId,
            section.RepoId,
            section.TaskId,
            section.ScopeType,
            section.Path,
            section.SectionsText,
            section.Source,
            section.SourcePath,
            section.ReviewState,
            section.UpdatedByType,
            section.UpdatedById,
            section.UpdatedAt);
    }

    public static IList<ContextSectionDto> MapToDtos(this IList<ContextSection> sections)
    {
        return sections.Select(s => s.MapToDto()).ToList();
    }
}
