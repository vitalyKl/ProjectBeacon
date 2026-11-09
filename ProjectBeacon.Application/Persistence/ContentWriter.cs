namespace ProjectBeacon.Application.Persistence;

using System.Text.Json;
using Domain.Entities.Context;
using Domain.Entities.Content;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
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
    }

    public void AddVersion(
        IBeaconDb db,
        object target,
        string? changedByType,
        string? changedById,
        bool deleted = false,
        Guid? proposalId = null)
    {
        var snapshot = target switch
        {
            ContextSection section => new
            {
                section.SectionId, section.Key, section.Title, section.BodyMarkdown, section.Ordinal,
                section.ScopeType, section.RepoId, section.TaskId, section.Path, Deleted = deleted
            },
            Decision decision => new
            {
                decision.Title, decision.Context, decision.DecisionBody, decision.Consequences,
                Deleted = deleted
            },
            Constraint constraint => new
            {
                constraint.Body, constraint.ScopePath, constraint.Kind, Deleted = deleted
            },
            Milestone milestone => new
            {
                milestone.Name, milestone.Description, milestone.Order, Deleted = deleted
            },
            Project project => new
            {
                project.Description, Deleted = deleted
            },
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
            _ => throw new ArgumentException(
                $"'{target.GetType().Name}' is not a revisioned content target.",
                nameof(target))
        };

        db.ContentVersions.Add(ContentVersion.Create(
            projectId,
            kind,
            targetId,
            revision,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            changedByType,
            changedById,
            proposalId));
    }

    private static int GetRevision(object target) => target switch
    {
        ContextSection section => section.Revision,
        Decision decision => decision.Revision,
        Constraint constraint => constraint.Revision,
        Milestone milestone => milestone.Revision,
        Project project => project.Revision,
        _ => throw new ArgumentException($"'{target.GetType().Name}' is not a revisioned content target.", nameof(target))
    };
}
