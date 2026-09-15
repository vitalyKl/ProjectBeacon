namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ListContextNodesHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListContextNodesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class GetContextNodeHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetContextNodeHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class DeleteContextNodeHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteContextNodeHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(DeleteContextNodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var section = await db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure<bool>("Context node not found.");

        db.ContextSections.Remove(section);
        await db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}