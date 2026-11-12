namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Audit;
using Application.Authorization;
using Application.Proposals;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Lists context sections for the project.
/// </summary>
public class ListContextNodesHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListContextNodesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ContextSectionDto>>> HandleAsync(ListContextNodesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var sections = await db.ContextSections
            .Where(s => s.ProjectId == command.Request.ProjectId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);

        return Result.Ok(sections.MapToDtos());
    }
}
/// <summary>
/// Loads one context section. Fails when it is missing.
/// </summary>
public class GetContextNodeHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetContextNodeHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ContextSectionDto>> HandleAsync(GetContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure<ContextSectionDto>("Context node not found.");

        return Result.Ok(section.MapToDto());
    }
}
/// <summary>
/// Deletes one context section.
/// </summary>
public class DeleteContextNodeHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;
    private readonly IAuditWriter? _auditWriter;

    public DeleteContextNodeHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter(), null)
    {
    }

    public DeleteContextNodeHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
        : this(dbFactory, contentWriter, null)
    {
    }

    public DeleteContextNodeHandler(
        IBeaconDbFactory dbFactory,
        ContentWriter contentWriter,
        IAuditWriter? auditWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(DeleteContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (command.Actor is not { IsHuman: true, UserId: not null } actor
            || !await ProposalProjectAuthorization.CanWriteAsync(
                db, actor, command.Request.ProjectId, ct))
            return Result.Forbidden("Project membership is required to delete a context section.");

        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure("Context node not found.");

        var orgId = await db.Projects
            .Where(project => project.Id == section.ProjectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return Result.Failure("Project not found.");

        section.Delete();
        db.ContextSections.Remove(section);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            actor, "content.deleted", nameof(ContentTargetKind.ContextSection),
            section.Id.ToString("D"), orgId, section.ProjectId));
        var attribution = ContentAttribution.FromActor(actor);
        var saved = await _contentWriter.SaveAsync(
            db, section, attribution.Type ?? "Human", attribution.Id, ct, deleted: true);
        return saved.Success
            ? Result.Ok()
            : Result.Failure(saved.Error ?? "Context section delete conflict.", ErrorKind.Conflict);
    }
}