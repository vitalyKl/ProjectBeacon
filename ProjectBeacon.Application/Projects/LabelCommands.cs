namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record MatchLabelRequest(Guid ProjectId, string Path);

public record AddLabelPathRequest(Guid ProjectId, Guid LabelId, string Path);

public record CreateLabelRequest(Guid ProjectId, string Name, string? Color, string? PathPrefix);

public record UpdateLabelRequest(Guid ProjectId, Guid LabelId, string? Name, string? Color, string? PathPrefix);

public record DeleteLabelRequest(Guid ProjectId, Guid LabelId);

public class MatchLabelHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public MatchLabelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LabelDto?>> HandleAsync(MatchLabelRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var labels = await db.Labels
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
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AddLabelPathHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LabelDto>> HandleAsync(AddLabelPathRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var path = PathMatcher.Normalize(request.Path);
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<LabelDto>("Path is required.");

        var label = await db.Labels
            .Include(l => l.Paths)
            .FirstOrDefaultAsync(l => l.Id == request.LabelId && l.ProjectId == request.ProjectId, ct);
        if (label is null)
            return Result.Failure<LabelDto>("Label not found.");

        if (!label.Paths.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            db.LabelPaths.Add(LabelPath.Create(label.Id, path, label.ProjectId));
        if (string.IsNullOrWhiteSpace(label.PathPrefix))
            label.SetPathPrefix(path);
        await db.SaveChangesAsync(ct);
        return Result.Ok(new LabelDto(label.Id, label.Name, label.Color, label.PathPrefix, label.ProjectId));
    }
}

public class CreateLabelHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateLabelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LabelDto>> HandleAsync(CreateLabelRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<LabelDto>("Name is required.");

        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.AnyAsync(p => p.Id == request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<LabelDto>("Project not found.");

        var color = string.IsNullOrWhiteSpace(request.Color) ? "#52525b" : request.Color.Trim();
        var prefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? null : PathMatcher.Normalize(request.PathPrefix);
        var label = Label.Create(request.Name.Trim(), color, request.ProjectId, prefix);
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        return Result.Ok(new LabelDto(label.Id, label.Name, label.Color, label.PathPrefix, label.ProjectId));
    }
}

public class UpdateLabelHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public UpdateLabelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LabelDto>> HandleAsync(UpdateLabelRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var label = await db.Labels
            .FirstOrDefaultAsync(l => l.Id == request.LabelId && l.ProjectId == request.ProjectId, ct);
        if (label is null)
            return Result.Failure<LabelDto>("Label not found.");

        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<LabelDto>("Name is required.");

        label.Update(request.Name?.Trim(), request.Color?.Trim());
        if (request.PathPrefix is not null)
            label.SetPathPrefix(PathMatcher.Normalize(request.PathPrefix));

        await db.SaveChangesAsync(ct);
        return Result.Ok(new LabelDto(label.Id, label.Name, label.Color, label.PathPrefix, label.ProjectId));
    }
}

public class DeleteLabelHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteLabelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(DeleteLabelRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var label = await db.Labels
            .FirstOrDefaultAsync(l => l.Id == request.LabelId && l.ProjectId == request.ProjectId, ct);
        if (label is null)
            return Result.Failure<bool>("Label not found.");

        var tasks = await db.Tasks.Where(t => t.LabelId == label.Id).ToListAsync(ct);
        foreach (var task in tasks)
            task.AssignLabel(null);

        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}