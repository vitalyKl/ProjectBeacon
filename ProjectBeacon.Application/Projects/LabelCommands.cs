namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record MatchLabelRequest(Guid ProjectId, string Path);

public record AddLabelPathRequest(Guid ProjectId, Guid LabelId, string Path);

public class MatchLabelHandler
{
    private readonly BeaconDbContext _db;

    public MatchLabelHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<LabelDto?>> HandleAsync(MatchLabelRequest request, CancellationToken ct = default)
    {
        var labels = await _db.Labels
            .Include(l => l.Paths)
            .Where(l => l.ProjectId == request.ProjectId)
            .ToListAsync(ct);
        var match = AutoLabel.Match(labels, request.Path);
        if (match is null)
            return Result.Ok<LabelDto?>(null);
        return Result.Ok<LabelDto?>(new LabelDto(match.Id, match.Name, match.Color, match.PathPrefix, match.ProjectId));
    }
}

public class AddLabelPathHandler
{
    private readonly BeaconDbContext _db;

    public AddLabelPathHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<LabelDto>> HandleAsync(AddLabelPathRequest request, CancellationToken ct = default)
    {
        var path = PathMatcher.Normalize(request.Path);
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<LabelDto>("Path is required.");

        var label = await _db.Labels
            .Include(l => l.Paths)
            .FirstOrDefaultAsync(l => l.Id == request.LabelId && l.ProjectId == request.ProjectId, ct);
        if (label is null)
            return Result.Failure<LabelDto>("Label not found.");

        label.AddPath(path);
        await _db.SaveChangesAsync(ct);
        return Result.Ok(new LabelDto(label.Id, label.Name, label.Color, label.PathPrefix, label.ProjectId));
    }
}
