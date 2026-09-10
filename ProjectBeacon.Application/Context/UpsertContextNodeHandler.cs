namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class UpsertContextNodeHandler
{
    private readonly BeaconDbContext _db;

    public UpsertContextNodeHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ContextSectionDto>> HandleAsync(UpsertContextNodeCommand command, CancellationToken ct = default)
    {
        var path = command.Request.Path ?? string.Empty;
        var sectionId = command.Request.SectionId
            ?? command.Request.Title.ToLowerInvariant().Replace(" ", "_");

        var existing = await _db.ContextSections
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
            existing.Update(
                title: command.Request.Title,
                bodyMarkdown: command.Request.BodyMarkdown,
                updatedByType: command.Request.Source == ContextSource.Native ? "user" : "system",
                updatedById: command.Request.SourcePath);

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
                source: command.Request.Source,
                sourcePath: command.Request.SourcePath,
                updatedByType: command.Request.Source == ContextSource.Native ? "user" : "system",
                updatedById: command.Request.SourcePath);

            _db.ContextSections.Add(section);
            savedId = section.Id;
        }

        await _db.SaveChangesAsync(ct);

        var savedSection = await _db.ContextSections.FirstOrDefaultAsync(s => s.Id == savedId, ct);

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
            section.UpdatedAt);
    }
}
