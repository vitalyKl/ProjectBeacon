namespace ProjectBeacon.Application.Context;

using Application.Common;
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

    public DeleteContextNodeHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public DeleteContextNodeHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result> HandleAsync(DeleteContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure("Context node not found.");

        section.Delete();
        db.ContextSections.Remove(section);
        var saved = await _contentWriter.SaveAsync(db, section, "Human", null, ct, deleted: true);
        return saved.Success
            ? Result.Ok()
            : Result.Failure(saved.Error ?? "Context section delete conflict.", ErrorKind.Conflict);
    }
}