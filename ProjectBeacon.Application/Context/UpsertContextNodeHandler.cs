namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Context;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates or updates a context section and reads it back.
/// </summary>
public class UpsertContextNodeHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public UpsertContextNodeHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public UpsertContextNodeHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public Task<Result<ContextSectionDto>> HandleAsync(UpsertContextNodeCommand command, CancellationToken ct = default)
        => HandleCoreAsync(command, isDraft: false, authorId: null, ct);

    public Task<Result<ContextSectionDto>> HandleDraftAsync(
        UpsertContextNodeCommand command, string? authorId, CancellationToken ct = default)
        => HandleCoreAsync(command, isDraft: true, authorId, ct);

    private async Task<Result<ContextSectionDto>> HandleCoreAsync(
        UpsertContextNodeCommand command, bool isDraft, string? authorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Request.Title) || string.IsNullOrWhiteSpace(command.Request.BodyMarkdown))
            return Result.Failure<ContextSectionDto>("Title and bodyMarkdown are required.");

        await using var db = _dbFactory.CreateDbContext();
        var path = command.Request.Path ?? string.Empty;
        var sectionId = command.Request.SectionId
            ?? command.Request.Title.ToLowerInvariant().Replace(" ", "_");

        var existing = await db.ContextSections
            .FirstOrDefaultAsync(s =>
                s.ProjectId == command.Request.ProjectId &&
                s.ScopeType == command.Request.ScopeType &&
                s.RepoId == command.Request.RepoId &&
                s.TaskId == command.Request.TaskId &&
                s.SectionId == sectionId &&
                s.Path == path, ct);

        Guid savedId;

        if (existing is not null)
        {
            if (isDraft
                && (existing.ReviewState != ContextReviewState.NeedsReview
                    || !string.Equals(existing.UpdatedByType, "agent", StringComparison.Ordinal)
                    || !string.Equals(existing.UpdatedById, authorId, StringComparison.Ordinal)))
                return Result.Failure<ContextSectionDto>(
                    "An agent draft may only update its own NeedsReview section; reviewed or other-author sections are protected.",
                    ErrorKind.Forbidden);

            var contentChanged =
                !string.Equals(existing.Title, command.Request.Title, StringComparison.Ordinal)
                || !string.Equals(existing.BodyMarkdown, command.Request.BodyMarkdown, StringComparison.Ordinal);
            if (contentChanged
                && command.Request.ExpectedRevision != existing.Revision)
                return Result.Failure<ContextSectionDto>(
                    "Context section changed since it was loaded. Refresh the section and retry.",
                    ErrorKind.Conflict);

            if (isDraft)
                existing.UpdateDraft(command.Request.Title, command.Request.BodyMarkdown,
                    command.Request.SourcePath, authorId);
            else
                existing.Update(
                    title: command.Request.Title,
                    bodyMarkdown: command.Request.BodyMarkdown,
                    updatedByType: command.Request.Source == ContextSource.Native ? "user" : "system",
                    updatedById: isDraft ? authorId : command.Actor?.UserId?.ToString("D") ?? command.Request.SourcePath);

            if (contentChanged)
            {
                var attribution = isDraft
                    ? ("Agent", authorId)
                    : command.Actor is not null
                        ? ContentAttribution.FromActor(command.Actor)
                        : command.Request.Source == ContextSource.Native
                            ? ("Human", command.Request.SourcePath)
                            : ("System", command.Request.SourcePath);
                var saved = await _contentWriter.SaveAsync(
                    db, existing, attribution.Item1, attribution.Item2, ct);
                if (!saved.Success)
                    return Result.Failure<ContextSectionDto>(saved.Error ?? "Content revision conflict.", ErrorKind.Conflict);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }

            savedId = existing.Id;
        }
        else
        {
            var section = ContextSection.Create(
                sectionId: sectionId,
                title: command.Request.Title,
                bodyMarkdown: command.Request.BodyMarkdown,
                projectId: command.Request.ProjectId,
                scopeType: command.Request.ScopeType,
                key: command.Request.Key,
                ordinal: 0,
                repoId: command.Request.RepoId,
                taskId: command.Request.TaskId,
                path: command.Request.Path,
                source: isDraft ? ContextSource.Native : command.Request.Source,
                sourcePath: command.Request.SourcePath,
                updatedByType: isDraft ? "agent" : command.Request.Source == ContextSource.Native ? "user" : "system",
                updatedById: isDraft ? authorId : command.Actor?.UserId?.ToString("D") ?? command.Request.SourcePath);

            db.ContextSections.Add(section);
            var attribution = isDraft
                ? ("Agent", authorId)
                : command.Actor is not null
                    ? ContentAttribution.FromActor(command.Actor)
                    : command.Request.Source == ContextSource.Native
                        ? ("Human", command.Request.SourcePath)
                        : ("System", command.Request.SourcePath);
            var saved = await _contentWriter.SaveAsync(
                db, section, attribution.Item1, attribution.Item2, ct);
            if (!saved.Success)
                return Result.Failure<ContextSectionDto>(saved.Error ?? "Content version could not be saved.");
            savedId = section.Id;
        }

        await db.SaveChangesAsync(ct);

        var savedSection = await db.ContextSections.FirstOrDefaultAsync(s => s.Id == savedId, ct);

        if (savedSection is null)
            return Result.Failure<ContextSectionDto>("Failed to retrieve saved section.");

        return Result.Ok(MapToDto(savedSection));
    }

    private static ContextSectionDto MapToDto(ContextSection section)
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
            section.UpdatedAt,
            section.Revision);
    }
}