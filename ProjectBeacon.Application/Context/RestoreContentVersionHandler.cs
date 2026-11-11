namespace ProjectBeacon.Application.Context;

using System.Text.Json;
using Application.Authorization;
using Application.Audit;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
using Domain.Entities.Content;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Restores an earlier content snapshot as a new revision. Physically deleted ContextSection
/// and Milestone targets are recreated only from an earlier snapshot when the latest target
/// revision is the matching deletion tombstone; a tombstone snapshot itself is never restored.
/// </summary>
public sealed class RestoreContentVersionHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;
    private readonly IAuditWriter? _auditWriter;
    private readonly IAuthorizationService? _authorization;

    public RestoreContentVersionHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter(), null, null)
    {
    }

    public RestoreContentVersionHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
        : this(dbFactory, contentWriter, null, null)
    {
    }

    public RestoreContentVersionHandler(
        IBeaconDbFactory dbFactory,
        ContentWriter contentWriter,
        IAuditWriter? auditWriter)
        : this(dbFactory, contentWriter, auditWriter, null)
    {
    }

    public RestoreContentVersionHandler(
        IBeaconDbFactory dbFactory,
        ContentWriter contentWriter,
        IAuditWriter? auditWriter,
        IAuthorizationService? authorization)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
        _auditWriter = auditWriter;
        _authorization = authorization;
    }

    public async Task<Result<RestoreContentVersionDto>> HandleAsync(
        RestoreContentVersionCommand command,
        CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.ContentVersionId == Guid.Empty)
            return Result.Failure<RestoreContentVersionDto>("ProjectId and ContentVersionId are required.");

        if (command.ExpectedRevision < 1)
            return Result.Failure<RestoreContentVersionDto>("ExpectedRevision must be greater than zero.");

        await using var db = _dbFactory.CreateDbContext();
        if (!command.Actor.IsHuman
            || command.Actor.UserId is null
            || !await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, command.ProjectId, ct))
            return Result.Forbidden<RestoreContentVersionDto>(
                "Project membership is required to restore content history.");

        var source = await db.ContentVersions.FirstOrDefaultAsync(
            v => v.Id == command.ContentVersionId && v.ProjectId == command.ProjectId, ct);
        if (source is null)
            return Result.Failure<RestoreContentVersionDto>("Content version not found.");

        // Restoring Decision/Constraint history can reinstate accepted governance state.
        // It therefore uses the same Owner/Admin boundary as approving governance proposals.
        if (source.TargetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
        {
            if (_authorization is null)
                return Result.Forbidden<RestoreContentVersionDto>(
                    "Decision and Constraint history can only be restored by a project or organization Owner/Admin.");

            var governance = await _authorization.CanAsync(
                db,
                command.Actor,
                ResourceType.Project,
                AuthAction.Administer,
                projectId: command.ProjectId,
                ct: ct);
            if (!governance.Success)
                return Result.Forbidden<RestoreContentVersionDto>(
                    "Decision and Constraint history can only be restored by a project or organization Owner/Admin.");
        }

        if (source.TargetKind == ContentTargetKind.TaskDescription
            && (command.ExpectedTargetKind != ContentTargetKind.TaskDescription
                || command.ExpectedTargetId is null))
            return Result.Failure<RestoreContentVersionDto>(
                "Task content history must be restored through a task-scoped operation.",
                ErrorKind.Forbidden);

        if (command.ExpectedTargetKind is { } expectedKind && source.TargetKind != expectedKind)
            return Result.Failure<RestoreContentVersionDto>("Content version does not belong to the requested target.");
        if (command.ExpectedTargetId is { } expectedTargetId && source.TargetId != expectedTargetId)
            return Result.Failure<RestoreContentVersionDto>("Content version does not belong to the requested target.");

        var attribution = ContentAttribution.FromActor(command.Actor);
        object target;
        var changed = false;
        var currentRevision = 0;
        var restoredTaskLinks = 0;

        try
        {
            switch (source.TargetKind)
            {
                case ContentTargetKind.ContextSection:
                    {
                        var snapshot = Deserialize<ContextSectionSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var section = await db.ContextSections.FirstOrDefaultAsync(
                            s => s.Id == source.TargetId && s.ProjectId == command.ProjectId, ct);
                        if (section is null)
                        {
                            var deletion = await GetLatestDeletionSnapshotAsync(
                                db, source, command.ExpectedRevision, ct);
                            if (deletion is null)
                                return MissingTargetFailure();

                            var sourceKind = snapshot.Source
                                ?? ReadSnapshotProperty<ContextSource>(deletion.ContentJson, "source")
                                ?? ContextSource.Native;
                            var sourcePath = HasSnapshotProperty(source.ContentJson, "sourcePath")
                                ? snapshot.SourcePath
                                : ReadSnapshotString(deletion.ContentJson, "sourcePath");
                            var reviewState = snapshot.ReviewState
                                ?? ReadSnapshotProperty<ContextReviewState>(deletion.ContentJson, "reviewState")
                                ?? ContextReviewState.NeedsReview;

                            section = ContextSection.RestoreDeleted(
                                source.TargetId,
                                command.ProjectId,
                                snapshot.SectionId,
                                snapshot.Key,
                                snapshot.Title,
                                snapshot.BodyMarkdown,
                                snapshot.Ordinal,
                                snapshot.ScopeType,
                                snapshot.RepoId,
                                snapshot.TaskId,
                                snapshot.Path,
                                sourceKind,
                                sourcePath,
                                reviewState,
                                checked(command.ExpectedRevision + 1),
                                attribution.Type ?? "Human",
                                attribution.Id);
                            db.ContextSections.Add(section);
                            target = section;
                            currentRevision = command.ExpectedRevision;
                            changed = true;
                            break;
                        }
                        if (section.Revision != command.ExpectedRevision)
                            return RevisionConflictFailure();

                        if (section.SectionId != snapshot.SectionId
                            || section.Key != snapshot.Key
                            || section.Ordinal != snapshot.Ordinal
                            || section.ScopeType != snapshot.ScopeType
                            || section.RepoId != snapshot.RepoId
                            || section.TaskId != snapshot.TaskId
                            || section.Path != snapshot.Path)
                            return UnsupportedStructureFailure();

                        currentRevision = section.Revision;
                        section.RestoreFromHistory(
                            snapshot.Title,
                            snapshot.BodyMarkdown,
                            snapshot.Source ?? section.Source,
                            snapshot.Source.HasValue ? snapshot.SourcePath : section.SourcePath,
                            snapshot.ReviewState ?? section.ReviewState,
                            updatedByType: command.Actor.IsHuman ? "user" : "system",
                            updatedById: attribution.Id);
                        target = section;
                        changed = section.Revision != currentRevision;
                        break;
                    }
                case ContentTargetKind.Decision:
                    {
                        var snapshot = Deserialize<DecisionSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var decision = await db.Decisions.FirstOrDefaultAsync(
                            d => d.Id == source.TargetId && d.ProjectId == command.ProjectId, ct);
                        if (decision is null)
                            return MissingTargetFailure();
                        if (decision.Revision != command.ExpectedRevision)
                            return RevisionConflictFailure();

                        currentRevision = decision.Revision;
                        decision.RestoreFromHistory(
                            snapshot.Title,
                            snapshot.Context,
                            snapshot.DecisionBody,
                            snapshot.Consequences,
                            snapshot.Status ?? decision.Status,
                            HasSnapshotProperty(source.ContentJson, "supersededById")
                                ? snapshot.SupersededById
                                : decision.SupersededById);
                        target = decision;
                        changed = decision.Revision != currentRevision;
                        break;
                    }
                case ContentTargetKind.Constraint:
                    {
                        var snapshot = Deserialize<ConstraintSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var constraint = await db.Constraints.FirstOrDefaultAsync(
                            c => c.Id == source.TargetId && c.ProjectId == command.ProjectId, ct);
                        if (constraint is null)
                            return MissingTargetFailure();
                        if (constraint.Revision != command.ExpectedRevision)
                            return RevisionConflictFailure();
                        if (constraint.Kind != snapshot.Kind)
                            return UnsupportedStructureFailure();

                        currentRevision = constraint.Revision;
                        constraint.RestoreFromHistory(
                            snapshot.Body, snapshot.ScopePath, snapshot.Status ?? constraint.Status);
                        target = constraint;
                        changed = constraint.Revision != currentRevision;
                        break;
                    }
                case ContentTargetKind.Milestone:
                    {
                        var snapshot = Deserialize<MilestoneSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var milestone = await db.Milestones.FirstOrDefaultAsync(
                            m => m.Id == source.TargetId && m.ProjectId == command.ProjectId, ct);
                        if (milestone is null)
                        {
                            var deletion = await GetLatestDeletionSnapshotAsync(
                                db, source, command.ExpectedRevision, ct);
                            if (deletion is null)
                                return MissingTargetFailure();

                            var closedAt = HasSnapshotProperty(source.ContentJson, "closedAt")
                                ? snapshot.ClosedAt
                                : ReadSnapshotProperty<DateTime>(deletion.ContentJson, "closedAt");
                            var createdAt = HasSnapshotProperty(source.ContentJson, "createdAt")
                                ? snapshot.CreatedAt
                                : ReadSnapshotProperty<DateTime>(deletion.ContentJson, "createdAt");

                            milestone = Milestone.RestoreDeleted(
                                source.TargetId,
                                snapshot.Name,
                                snapshot.Description,
                                command.ProjectId,
                                snapshot.Order,
                                checked(command.ExpectedRevision + 1),
                                closedAt,
                                createdAt);
                            db.Milestones.Add(milestone);

                            // Deleting a milestone sets task MilestoneId to null. Restore only
                            // links tasks that remain unassigned; a later deliberate move to a
                            // different milestone must not be overwritten by history restoration.
                            var taskIds = ReadSnapshotArray<Guid>(deletion.ContentJson, "taskIds")
                                ?? snapshot.TaskIds
                                ?? [];
                            if (taskIds.Length > 0)
                            {
                                var tasksToRelink = await db.Tasks
                                    .Where(task => task.ProjectId == command.ProjectId
                                        && task.MilestoneId == null
                                        && taskIds.Contains(task.Id))
                                    .ToListAsync(ct);
                                foreach (var task in tasksToRelink)
                                    task.SetMilestone(milestone.Id);
                                restoredTaskLinks = tasksToRelink.Count;
                            }

                            target = milestone;
                            currentRevision = command.ExpectedRevision;
                            changed = true;
                            break;
                        }
                        if (milestone.Revision != command.ExpectedRevision)
                            return RevisionConflictFailure();

                        currentRevision = milestone.Revision;
                        milestone.RestoreContentFromHistory(
                            snapshot.Name,
                            snapshot.Description,
                            snapshot.Order,
                            HasSnapshotProperty(source.ContentJson, "closedAt")
                                ? snapshot.ClosedAt
                                : milestone.ClosedAt);
                        target = milestone;
                        changed = milestone.Revision != currentRevision;
                        break;
                    }
                case ContentTargetKind.ProjectDescription:
                    {
                        var snapshot = Deserialize<ProjectDescriptionSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var project = await db.Projects.FirstOrDefaultAsync(
                            p => p.Id == command.ProjectId, ct);
                        if (project is null)
                            return MissingTargetFailure();
                        if (project.Revision != command.ExpectedRevision)
                            return RevisionConflictFailure();

                        currentRevision = project.Revision;
                        project.RestoreDescriptionFromHistory(snapshot.Description);
                        target = project;
                        changed = project.Revision != currentRevision;
                        break;
                    }
                case ContentTargetKind.TaskDescription:
                    {
                        var snapshot = Deserialize<TaskDescriptionSnapshot>(source.ContentJson);
                        if (snapshot.Deleted)
                            return DeletedSnapshotFailure();

                        var task = await db.Tasks.FirstOrDefaultAsync(
                            t => t.Id == source.TargetId && t.ProjectId == command.ProjectId, ct);
                        if (task is null)
                            return MissingTargetFailure();
                        if (task.ContentRevision != command.ExpectedRevision)
                            return RevisionConflictFailure();

                        currentRevision = task.ContentRevision;
                        task.RestoreContentFromHistory(snapshot.Description, snapshot.ReviewNotes);
                        target = task;
                        changed = task.ContentRevision != currentRevision;
                        break;
                    }
                default:
                    return Result.Failure<RestoreContentVersionDto>("Unsupported content target kind.");
            }
        }
        catch (JsonException)
        {
            return Result.Failure<RestoreContentVersionDto>("The stored content snapshot is invalid.");
        }

        if (!changed)
            return Result.Ok(new RestoreContentVersionDto(
                source.Id, source.TargetKind, source.TargetId, currentRevision, false));

        var restoredRevision = checked(currentRevision + 1);
        if (_auditWriter is not null)
        {
            var orgId = await db.Projects
                .Where(project => project.Id == command.ProjectId)
                .Select(project => (Guid?)project.OrgId)
                .FirstOrDefaultAsync(ct);
            if (orgId is null)
                return Result.Failure<RestoreContentVersionDto>("Project not found.");

            var auditDetails = JsonSerializer.Serialize(new
            {
                sourceVersionId = source.Id,
                restoredRevision,
                restoredTaskLinks
            }, JsonOptions);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor,
                "content.restored",
                source.TargetKind.ToString(),
                source.TargetId.ToString("D"),
                orgId,
                command.ProjectId,
                detailsJson: auditDetails));
        }

        var saved = await _contentWriter.SaveAsync(
            db, target, attribution.Type ?? "Human", attribution.Id, ct);
        if (!saved.Success)
            return Result.Failure<RestoreContentVersionDto>(
                saved.Error ?? "Content restore conflict.", ErrorKind.Conflict);

        return Result.Ok(new RestoreContentVersionDto(
            source.Id, source.TargetKind, source.TargetId, saved.Value, true));
    }

    private static bool HasSnapshotProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out _);
    }

    private static async Task<ContentVersion?> GetLatestDeletionSnapshotAsync(
        IBeaconDb db,
        ContentVersion source,
        int expectedRevision,
        CancellationToken ct)
    {
        var latest = await db.ContentVersions
            .Where(version => version.ProjectId == source.ProjectId
                && version.TargetKind == source.TargetKind
                && version.TargetId == source.TargetId)
            .OrderByDescending(version => version.Revision)
            .FirstOrDefaultAsync(ct);
        if (latest is null || latest.Revision != expectedRevision || latest.Revision <= source.Revision)
            return null;

        try
        {
            using var document = JsonDocument.Parse(latest.ContentJson);
            return document.RootElement.TryGetProperty("deleted", out var deleted)
                && deleted.ValueKind == JsonValueKind.True
                    ? latest
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T[]? ReadSnapshotArray<T>(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return null;
        return value.Deserialize<T[]>(JsonOptions) ?? [];
    }

    private static T? ReadSnapshotProperty<T>(string json, string propertyName)
        where T : struct
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.Deserialize<T>(JsonOptions);
    }

    private static string? ReadSnapshotString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new JsonException("The content snapshot is empty.");

    private static Result<RestoreContentVersionDto> DeletedSnapshotFailure() =>
        Result.Failure<RestoreContentVersionDto>(
            "A deletion snapshot cannot be restored. Select an earlier content snapshot.");

    private static Result<RestoreContentVersionDto> MissingTargetFailure() =>
        Result.Failure<RestoreContentVersionDto>(
            "The content target no longer exists and has no matching latest deletion snapshot to restore.");

    private static Result<RestoreContentVersionDto> RevisionConflictFailure() =>
        Result.Failure<RestoreContentVersionDto>(
            "Content changed since it was loaded. Refresh the target and retry.",
            ErrorKind.Conflict);

    private static Result<RestoreContentVersionDto> UnsupportedStructureFailure() =>
        Result.Failure<RestoreContentVersionDto>(
            "The historical snapshot contains immutable target fields that cannot be restored.");

    private sealed record ContextSectionSnapshot(
        string SectionId, string Key, string Title, string BodyMarkdown, int Ordinal,
        ContextScopeType ScopeType, Guid? RepoId, Guid? TaskId, string Path,
        ContextSource? Source = null,
        string? SourcePath = null,
        ContextReviewState? ReviewState = null,
        string? UpdatedByType = null,
        string? UpdatedById = null,
        bool Deleted = false);
    private sealed record DecisionSnapshot(
        string Title, string Context, string DecisionBody, string? Consequences,
        DecisionStatus? Status = null,
        Guid? SupersededById = null,
        bool Deleted = false);
    private sealed record ConstraintSnapshot(
        string Body, string ScopePath, ConstraintKind Kind,
        ConstraintStatus? Status = null,
        bool Deleted = false);
    private sealed record MilestoneSnapshot(
        string Name,
        string? Description,
        int Order,
        DateTime? CreatedAt = null,
        DateTime? ClosedAt = null,
        Guid[]? TaskIds = null,
        bool Deleted = false);
    private sealed record ProjectDescriptionSnapshot(string? Description, bool Deleted = false);
    private sealed record TaskDescriptionSnapshot(
        string? Description, string? ReviewNotes, bool Deleted = false);
}

/// <summary>Result of restoring a historical snapshot.</summary>
public sealed record RestoreContentVersionDto(
    Guid SourceVersionId,
    ContentTargetKind TargetKind,
    Guid TargetId,
    int Revision,
    bool Changed);

/// <summary>Restores the source snapshot after checking the caller's current revision.</summary>
public sealed record RestoreContentVersionCommand(
    Guid ProjectId,
    Guid ContentVersionId,
    int ExpectedRevision,
    ActorContext Actor,
    ContentTargetKind? ExpectedTargetKind = null,
    Guid? ExpectedTargetId = null) : ICommand<Result<RestoreContentVersionDto>>;
