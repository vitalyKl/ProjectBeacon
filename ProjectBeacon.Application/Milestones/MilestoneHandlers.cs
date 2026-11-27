namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Application.Authorization;
using Application.Proposals;
using Application.Audit;
using Application.Devices;
using Application.Releases;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a milestone on a project.
/// </summary>
public class CreateMilestoneHandler : ICommandHandler<CreateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CreateMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CreateMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CreateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.IgnoreQueryFilters()
            .AnyAsync(project => project.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<MilestoneDto>("Project not found.");
        if (command.Actor is not { IsHuman: true, UserId: not null } actor
            || !await ProposalProjectAuthorization.CanWriteAsync(
                db, actor, command.Request.ProjectId, ct))
            return Result.Forbidden<MilestoneDto>("Project membership is required to create a milestone.");

        var milestone = Domain.Entities.Roadmap.Milestone.Create(
            command.Request.Name,
            command.Request.Description,
            command.Request.ProjectId,
            command.Request.Order);

        db.Milestones.Add(milestone);
        var attribution = ContentAttribution.FromActor(command.Actor);
        var saved = await _contentWriter.SaveAsync(db, milestone, attribution.Type, attribution.Id, ct);
        if (!saved.Success)
            return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone version could not be saved.", ErrorKind.Conflict);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision, milestone.ReleaseId);
}
/// <summary>
/// Updates a milestone name, description, or order.
/// </summary>
public class UpdateMilestoneHandler : ICommandHandler<UpdateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public UpdateMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public UpdateMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(UpdateMilestoneCommand command, CancellationToken ct = default)
    {
        return await MilestoneMutationCoordinator.ExecuteAsync(
            _dbFactory,
            command.Request.MilestoneId,
            command.Request.ProjectId,
            async (db, milestone, token) =>
            {
                if (command.Actor is not { IsHuman: true, UserId: not null } actor
                    || !await ProposalProjectAuthorization.CanWriteAsync(db, actor, milestone.ProjectId, token))
                    return Result.Forbidden<MilestoneDto>("Project membership is required to update a milestone.");

                var contentChanged =
                    (command.Request.Name is not null && !string.Equals(milestone.Name, command.Request.Name, StringComparison.Ordinal))
                    || (command.Request.Description is not null && !string.Equals(milestone.Description, command.Request.Description, StringComparison.Ordinal))
                    || (command.Request.Order is not null && milestone.Order != command.Request.Order.Value);
                if (contentChanged && command.Request.ExpectedRevision != milestone.Revision)
                    return Result.Failure<MilestoneDto>(
                        "Milestone changed since it was loaded. Refresh it and retry.",
                        ErrorKind.Conflict);

                milestone.Update(command.Request.Name, command.Request.Description, command.Request.Order);
                if (contentChanged)
                {
                    var linkedTaskIds = await db.Tasks
                        .Where(task => task.ProjectId == milestone.ProjectId && task.MilestoneId == milestone.Id)
                        .Select(task => task.Id)
                        .ToArrayAsync(token);
                    var attribution = ContentAttribution.FromActor(command.Actor);
                    var saved = await _contentWriter.SaveAsync(
                        db, milestone, attribution.Type ?? "Human", attribution.Id, token,
                        milestoneTaskIds: linkedTaskIds);
                    if (!saved.Success)
                        return Result.Failure<MilestoneDto>(
                            saved.Error ?? "Milestone content conflict.", saved.Kind);
                }
                else
                {
                    await db.SaveChangesAsync(token);
                }

                return Result.Ok(MapToDto(milestone));
            },
            ct);
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt,
            milestone.Revision, milestone.ReleaseId);
}
/// <summary>
/// Deletes a milestone.
/// </summary>
public class DeleteMilestoneHandler : ICommandHandler<DeleteMilestoneCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;
    private readonly IAuditWriter? _auditWriter;

    public DeleteMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter(), null)
    {
    }

    public DeleteMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
        : this(dbFactory, contentWriter, null)
    {
    }

    public DeleteMilestoneHandler(
        IBeaconDbFactory dbFactory,
        ContentWriter contentWriter,
        IAuditWriter? auditWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(DeleteMilestoneCommand command, CancellationToken ct = default)
    {
        var result = await MilestoneMutationCoordinator.ExecuteAsync<bool>(
            _dbFactory,
            command.Request.MilestoneId,
            command.Request.ProjectId,
            async (db, milestone, token) =>
            {
                if (command.Actor is not { IsHuman: true, UserId: not null } actor
                    || !await ProposalProjectAuthorization.CanWriteAsync(db, actor, milestone.ProjectId, token))
                    return Result.Forbidden<bool>("Project membership is required to delete a milestone.");

                var orgId = await db.Projects.IgnoreQueryFilters()
                    .Where(project => project.Id == milestone.ProjectId)
                    .Select(project => (Guid?)project.OrgId)
                    .FirstOrDefaultAsync(token);
                if (orgId is null)
                    return Result.Failure<bool>("Project not found.", ErrorKind.NotFound);

                var linkedTaskIds = await db.Tasks
                    .Where(task => task.ProjectId == milestone.ProjectId && task.MilestoneId == milestone.Id)
                    .Select(task => task.Id)
                    .ToArrayAsync(token);

                milestone.Delete();
                db.Milestones.Remove(milestone);
                _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                    actor, "content.deleted", nameof(ContentTargetKind.Milestone),
                    milestone.Id.ToString("D"), orgId, milestone.ProjectId));
                var attribution = ContentAttribution.FromActor(actor);
                var saved = await _contentWriter.SaveAsync(
                    db, milestone, attribution.Type ?? "Human", attribution.Id, token,
                    deleted: true, milestoneTaskIds: linkedTaskIds);
                return saved.Success
                    ? Result.Ok(true)
                    : Result.Failure<bool>(saved.Error ?? "Milestone delete conflict.", saved.Kind);
            },
            ct);

        return result.Success
            ? Result.Ok()
            : Result.Failure(result.Error ?? "Milestone delete conflict.", result.Kind);
    }
}
/// <summary>
/// Loads one milestone.
/// </summary>
public class GetMilestoneHandler : ICommandHandler<GetMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetMilestoneHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(GetMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones
            .FirstOrDefaultAsync(item => item.Id == command.Request.MilestoneId
                && (command.Request.ProjectId == null || item.ProjectId == command.Request.ProjectId), ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.", ErrorKind.NotFound);
        if (!await ProposalProjectAuthorization.CanWriteAsync(
                db, command.Actor, milestone.ProjectId, ct))
            return Result.Forbidden<MilestoneDto>("Project membership is required to read a milestone.");

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision, milestone.ReleaseId);
}
/// <summary>
/// Lists milestones for a project.
/// </summary>
public class ListProjectMilestonesHandler : ICommandHandler<ListProjectMilestonesCommand, Result<IList<MilestoneDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProjectMilestonesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<MilestoneDto>>> HandleAsync(ListProjectMilestonesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.IgnoreQueryFilters().AnyAsync(
            project => project.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<IList<MilestoneDto>>("Project not found.", ErrorKind.NotFound);
        if (!await ProposalProjectAuthorization.CanWriteAsync(
                db, command.Actor, command.Request.ProjectId, ct))
            return Result.Forbidden<IList<MilestoneDto>>(
                "Project membership is required to list milestones.");

        var milestones = await db.Milestones
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderBy(m => m.Order)
            .Select(m => new MilestoneDto(
                m.Id, m.Name, m.Description, m.ProjectId, m.Order, m.CreatedAt, m.UpdatedAt, m.ClosedAt, m.Revision, m.ReleaseId))
            .ToListAsync(ct);

        return Result.Ok((IList<MilestoneDto>)milestones);
    }
}
/// <summary>
/// Closes a milestone.
/// </summary>
public class CloseMilestoneHandler : ICommandHandler<CloseMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CloseMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CloseMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CloseMilestoneCommand command, CancellationToken ct = default)
    {
        return await MilestoneMutationCoordinator.ExecuteAsync(
            _dbFactory,
            command.Request.MilestoneId,
            command.Request.ProjectId,
            async (db, milestone, token) =>
            {
                if (command.Actor is not { IsHuman: true, UserId: not null } actor
                    || !await ProposalProjectAuthorization.CanWriteAsync(db, actor, milestone.ProjectId, token))
                    return Result.Forbidden<MilestoneDto>("Project membership is required to close a milestone.");

                var previousRevision = milestone.Revision;
                milestone.Close();
                if (milestone.Revision != previousRevision)
                {
                    var linkedTaskIds = await db.Tasks
                        .Where(task => task.ProjectId == milestone.ProjectId && task.MilestoneId == milestone.Id)
                        .Select(task => task.Id)
                        .ToArrayAsync(token);
                    var attribution = ContentAttribution.FromActor(command.Actor);
                    var saved = await _contentWriter.SaveAsync(
                        db, milestone, attribution.Type ?? "Human", attribution.Id, token,
                        milestoneTaskIds: linkedTaskIds);
                    if (!saved.Success)
                        return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone close conflict.", saved.Kind);
                }
                return Result.Ok(new MilestoneDto(
                    milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
                    milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt,
                    milestone.Revision, milestone.ReleaseId));
            },
            ct);
    }
}
/// <summary>
/// Clears a milestone's closed time.
/// </summary>
public class ReopenMilestoneHandler : ICommandHandler<ReopenMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public ReopenMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public ReopenMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(ReopenMilestoneCommand command, CancellationToken ct = default)
    {
        return await MilestoneMutationCoordinator.ExecuteAsync(
            _dbFactory,
            command.Request.MilestoneId,
            command.Request.ProjectId,
            async (db, milestone, token) =>
            {
                if (command.Actor is not { IsHuman: true, UserId: not null } actor
                    || !await ProposalProjectAuthorization.CanWriteAsync(db, actor, milestone.ProjectId, token))
                    return Result.Forbidden<MilestoneDto>("Project membership is required to reopen a milestone.");

                var previousRevision = milestone.Revision;
                milestone.Reopen();
                if (milestone.Revision != previousRevision)
                {
                    var linkedTaskIds = await db.Tasks
                        .Where(task => task.ProjectId == milestone.ProjectId && task.MilestoneId == milestone.Id)
                        .Select(task => task.Id)
                        .ToArrayAsync(token);
                    var attribution = ContentAttribution.FromActor(command.Actor);
                    var saved = await _contentWriter.SaveAsync(
                        db, milestone, attribution.Type ?? "Human", attribution.Id, token,
                        milestoneTaskIds: linkedTaskIds);
                    if (!saved.Success)
                        return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone reopen conflict.", saved.Kind);
                }
                return Result.Ok(new MilestoneDto(
                    milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
                    milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt,
                    milestone.Revision, milestone.ReleaseId));
            },
            ct);
    }
}

/// <summary>
/// Serializes every mutation of an existing milestone with ReleasePrepare and applies the
/// immutable Candidate-scope guard before invoking the handler-specific mutation.
/// </summary>
internal static class MilestoneMutationCoordinator
{
    public static async Task<Result<T>> ExecuteAsync<T>(
        IBeaconDbFactory dbFactory,
        Guid milestoneId,
        Guid? projectId,
        Func<IBeaconDb, Milestone, CancellationToken, Task<Result<T>>> mutation,
        CancellationToken ct)
    {
        if (milestoneId == Guid.Empty)
            return Result.Failure<T>("Milestone id is required.", ErrorKind.Validation);

        await using var db = dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var scope = await db.Milestones.AsNoTracking()
                .Where(item => item.Id == milestoneId
                    && (projectId == null || item.ProjectId == projectId.Value))
                .Select(item => new { item.ProjectId })
                .FirstOrDefaultAsync(ct);
            if (scope is null)
                return Result.Failure<T>("Milestone not found.", ErrorKind.NotFound);

            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, scope.ProjectId, ct))
                return Result.Failure<T>("Milestone not found.", ErrorKind.NotFound);

            var milestone = await db.Milestones
                .FirstOrDefaultAsync(item => item.Id == milestoneId && item.ProjectId == scope.ProjectId, ct);
            if (milestone is null)
                return Result.Failure<T>("Milestone not found.", ErrorKind.NotFound);

            var blockReason = await ReleaseMilestoneMutationGuard.GetBlockReasonAsync(
                db, milestone.ProjectId, milestone.Id, ct);
            if (blockReason is not null)
                return Result.Failure<T>(blockReason, ErrorKind.Conflict);

            var result = await mutation(db, milestone, ct);
            if (!result.Success)
                return result;

            await transaction.CommitAsync(ct);
            return result;
        });
    }
}

