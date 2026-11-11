namespace ProjectBeacon.Application.Persistence;

using System.Text.Json;
using Domain.Entities.Context;
using Domain.Entities.Content;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Application.Common;

public sealed class ContentWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<int>> SaveAsync(
        IBeaconDb db,
        object target,
        string? changedByType,
        string? changedById,
        CancellationToken ct = default,
        bool deleted = false,
        Guid? proposalId = null)
    {
        AddVersion(db, target, changedByType, changedById, deleted, proposalId);
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Ok(GetRevision(target));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<int>(
                "Content changed since it was loaded. Refresh the item and retry.",
                ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            // Two writers can both read revision N and race to insert the same N+1
            // snapshot. Depending on EF command ordering, PostgreSQL may report the
            // unique ContentVersions index before the target row's concurrency token.
            // Treat that specific duplicate revision as the same stale-write conflict.
            var (projectId, targetId, kind, revision) = GetIdentity(target);
            var duplicateRevision = await db.ContentVersions.AnyAsync(
                version => version.ProjectId == projectId
                    && version.TargetId == targetId
                    && version.TargetKind == kind
                    && version.Revision == revision,
                ct);
            if (duplicateRevision)
            {
                return Result.Failure<int>(
                    "Content changed since it was loaded. Refresh the item and retry.",
                    ErrorKind.Conflict);
            }

            throw;
        }
    }

    public void AddVersion(
        IBeaconDb db,
        object target,
        string? changedByType,
        string? changedById,
        bool deleted = false,
        Guid? proposalId = null)
    {
        var snapshotJson = target switch
        {
            ContextSection section => JsonSerializer.Serialize(new
            {
                section.SectionId,
                section.Key,
                section.Title,
                section.BodyMarkdown,
                section.Ordinal,
                section.ScopeType,
                section.RepoId,
                section.TaskId,
                section.Path,
                section.Source,
                section.SourcePath,
                section.ReviewState,
                section.UpdatedByType,
                section.UpdatedById,
                Deleted = deleted
            }, JsonOptions),
            Decision decision => JsonSerializer.Serialize(new
            {
                decision.Title,
                decision.Context,
                decision.DecisionBody,
                decision.Consequences,
                decision.Status,
                decision.SupersededById,
                Deleted = deleted
            }, JsonOptions),
            Constraint constraint => JsonSerializer.Serialize(new
            {
                constraint.Body,
                constraint.ScopePath,
                constraint.Kind,
                constraint.Status,
                Deleted = deleted
            }, JsonOptions),
            Milestone milestone => JsonSerializer.Serialize(new
            {
                milestone.Name,
                milestone.Description,
                milestone.Order,
                milestone.CreatedAt,
                milestone.ClosedAt,
                Deleted = deleted
            }, JsonOptions),
            Project project => JsonSerializer.Serialize(new
            {
                project.Description,
                Deleted = deleted
            }, JsonOptions),
            TaskItem task => JsonSerializer.Serialize(new
            {
                task.Description,
                task.ReviewNotes,
                Deleted = deleted
            }, JsonOptions),
            _ => throw new ArgumentException(
                $"'{target.GetType().Name}' is not a revisioned content target.",
                nameof(target))
        };

        var (projectId, targetId, kind, revision) = target switch
        {
            ContextSection section => (section.ProjectId, section.Id, ContentTargetKind.ContextSection, section.Revision),
            Decision decision => (decision.ProjectId, decision.Id, ContentTargetKind.Decision, decision.Revision),
            Constraint constraint => (constraint.ProjectId, constraint.Id, ContentTargetKind.Constraint, constraint.Revision),
            Milestone milestone => (milestone.ProjectId, milestone.Id, ContentTargetKind.Milestone, milestone.Revision),
            Project project => (project.Id, project.Id, ContentTargetKind.ProjectDescription, project.Revision),
            TaskItem task => (task.ProjectId, task.Id, ContentTargetKind.TaskDescription, task.ContentRevision),
            _ => throw new ArgumentException(
                $"'{target.GetType().Name}' is not a revisioned content target.",
                nameof(target))
        };

        db.ContentVersions.Add(ContentVersion.Create(
            projectId,
            kind,
            targetId,
            revision,
            snapshotJson,
            changedByType,
            changedById,
            proposalId));
    }

    private static (Guid ProjectId, Guid TargetId, ContentTargetKind TargetKind, int Revision) GetIdentity(object target) => target switch
    {
        ContextSection section => (section.ProjectId, section.Id, ContentTargetKind.ContextSection, section.Revision),
        Decision decision => (decision.ProjectId, decision.Id, ContentTargetKind.Decision, decision.Revision),
        Constraint constraint => (constraint.ProjectId, constraint.Id, ContentTargetKind.Constraint, constraint.Revision),
        Milestone milestone => (milestone.ProjectId, milestone.Id, ContentTargetKind.Milestone, milestone.Revision),
        Project project => (project.Id, project.Id, ContentTargetKind.ProjectDescription, project.Revision),
        TaskItem task => (task.ProjectId, task.Id, ContentTargetKind.TaskDescription, task.ContentRevision),
        _ => throw new ArgumentException($"'{target.GetType().Name}' is not a revisioned content target.", nameof(target))
    };

    private static int GetRevision(object target) => target switch
    {
        ContextSection section => section.Revision,
        Decision decision => decision.Revision,
        Constraint constraint => constraint.Revision,
        Milestone milestone => milestone.Revision,
        Project project => project.Revision,
        TaskItem task => task.ContentRevision,
        _ => throw new ArgumentException($"'{target.GetType().Name}' is not a revisioned content target.", nameof(target))
    };
}
