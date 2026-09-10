namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ListContextNodesHandler
{
    private readonly BeaconDbContext _db;

    public ListContextNodesHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<ContextSectionDto>>> HandleAsync(ListContextNodesCommand command, CancellationToken ct = default)
    {
        var sections = await _db.ContextSections
            .Where(s => s.ProjectId == command.Request.ProjectId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(ct);

        return Result.Ok(sections.MapToDtos());
    }
}

public class GetContextNodeHandler
{
    private readonly BeaconDbContext _db;

    public GetContextNodeHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ContextSectionDto>> HandleAsync(GetContextNodeCommand command, CancellationToken ct = default)
    {
        var section = await _db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure<ContextSectionDto>("Context node not found.");

        return Result.Ok(section.MapToDto());
    }
}

public class DeleteContextNodeHandler
{
    private readonly BeaconDbContext _db;

    public DeleteContextNodeHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<bool>> HandleAsync(DeleteContextNodeCommand command, CancellationToken ct = default)
    {
        var section = await _db.ContextSections
            .FirstOrDefaultAsync(s => s.Id == command.Request.NodeId && s.ProjectId == command.Request.ProjectId, ct);

        if (section is null)
            return Result.Failure<bool>("Context node not found.");

        _db.ContextSections.Remove(section);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}
