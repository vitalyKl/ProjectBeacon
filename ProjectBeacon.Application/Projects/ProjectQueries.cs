namespace ProjectBeacon.Application.Projects;

using System.Security.Claims;
using Application.Common;
using Application.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record GetCurrentProjectQuery : IQuery<Result<ProjectDto?>>;

public class GetCurrentProjectHandler
{
    private readonly BeaconDbContext _db;

    public GetCurrentProjectHandler(BeaconDbContext db) => _db = db;

    public Task<Result<ProjectDto?>> HandleAsync(CancellationToken ct = default)
        => HandleAsync(userId: null, isAdmin: false, ct);

    public Task<Result<ProjectDto?>> HandleAsync(ClaimsPrincipal? user, CancellationToken ct = default)
    {
        Guid? userId = null;
        var isAdmin = false;
        if (user?.Identity?.IsAuthenticated == true)
        {
            if (Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id))
                userId = id;
            isAdmin = bool.TryParse(user.FindFirst("isAdmin")?.Value, out var flag) && flag;
        }

        return HandleAsync(userId, isAdmin, ct);
    }

    public async Task<Result<ProjectDto?>> HandleAsync(Guid? userId, bool isAdmin, CancellationToken ct = default)
    {
        if (userId is { } uid)
        {
            var (projectId, _) = await CurrentProjectLookup.ForUserAsync(_db, uid, isAdmin, ct);
            if (projectId is null)
                return Result.Ok<ProjectDto?>(null);

            var found = await _db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == projectId.Value, ct);
            if (found is null)
                return Result.Ok<ProjectDto?>(null);

            return Result.Ok<ProjectDto?>(MapToDto(found));
        }

        var project = await _db.Projects.OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        if (project is null)
            return Result.Ok<ProjectDto?>(null);

        return Result.Ok<ProjectDto?>(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);
}

public record DashboardCountsDto(int Projects, int Tasks, int InProgress, int Done);

public class GetDashboardCountsHandler
{
    private readonly BeaconDbContext _db;

    public GetDashboardCountsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<DashboardCountsDto>> HandleAsync(CancellationToken ct = default)
    {
        var projects = await _db.Projects.CountAsync(ct);
        var tasks = await _db.Tasks.Select(t => t.Status).ToListAsync(ct);
        return Result.Ok(new DashboardCountsDto(
            projects,
            tasks.Count,
            tasks.Count(s => s == TaskItemStatus.InProgress),
            tasks.Count(s => s == TaskItemStatus.Done)));
    }
}

public record LabelDto(Guid Id, string Name, string Color, string PathPrefix, Guid ProjectId);

public record ListLabelsRequest(Guid ProjectId);

public class ListLabelsHandler
{
    private readonly BeaconDbContext _db;

    public ListLabelsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<LabelDto>>> HandleAsync(ListLabelsRequest request, CancellationToken ct = default)
    {
        var labels = await _db.Labels
            .Where(l => l.ProjectId == request.ProjectId)
            .OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color, l.PathPrefix, l.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<LabelDto>)labels);
    }
}
