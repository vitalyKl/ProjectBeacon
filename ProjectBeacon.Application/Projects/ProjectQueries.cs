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
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetCurrentProjectHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public Task<Result<ProjectDto?>> HandleAsync(CancellationToken ct = default)
        => HandleAsync(userId: null, isAdmin: false, preferredProjectId: null, ct);

    public Task<Result<ProjectDto?>> HandleAsync(ClaimsPrincipal? user, CancellationToken ct = default)
    {
        Guid? userId = null;
        var isAdmin = false;
        Guid? preferred = null;
        if (user?.Identity?.IsAuthenticated == true)
        {
            if (Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id))
                userId = id;
            isAdmin = bool.TryParse(user.FindFirst("isAdmin")?.Value, out var flag) && flag;
            if (Guid.TryParse(user.FindFirst("project_id")?.Value, out var claimed))
                preferred = claimed;
        }

        return HandleAsync(userId, isAdmin, preferred, ct);
    }

    public async Task<Result<ProjectDto?>> HandleAsync(
        Guid? userId, bool isAdmin, Guid? preferredProjectId = null, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (userId is { } uid)
        {
            var (projectId, _) = await CurrentProjectLookup.ForUserAsync(db, uid, isAdmin, preferredProjectId, ct);
            if (projectId is null)
                return Result.Ok<ProjectDto?>(null);

            var found = await db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == projectId.Value, ct);
            if (found is null)
                return Result.Ok<ProjectDto?>(null);

            return Result.Ok<ProjectDto?>(MapToDto(found));
        }

        var project = await db.Projects.OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);
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
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetDashboardCountsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DashboardCountsDto>> HandleAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projects = await db.Projects.CountAsync(ct);
        var tasks = await db.Tasks.Select(t => t.Status).ToListAsync(ct);
        return Result.Ok(new DashboardCountsDto(
            projects,
            tasks.Count,
            tasks.Count(s => s == TaskItemStatus.InProgress),
            tasks.Count(s => s == TaskItemStatus.Done)));
    }
}

public record ProjectSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    Guid OrgId,
    int TotalTasks,
    int Todo,
    int InProgress,
    int Done,
    DateTime LastActivity);

public record RecentTaskDto(
    Guid Id,
    string Title,
    TaskItemStatus Status,
    Guid ProjectId,
    string ProjectName,
    DateTime ActivityAt);

public record ListMyProjectsDto(
    int TotalProjects,
    int TotalTasks,
    int InProgress,
    int Done,
    IReadOnlyList<ProjectSummaryDto> Projects,
    IReadOnlyList<RecentTaskDto> RecentTasks);

public class ListMyProjectsHandler
{
    private const int RecentLimit = 10;

    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListMyProjectsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ListMyProjectsDto>> HandleAsync(
        Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectIds = isAdmin
            ? await db.Projects.IgnoreQueryFilters().Select(p => p.Id).ToListAsync(ct)
            : await db.ProjectMembers.IgnoreQueryFilters()
                .Where(m => m.UserId == userId)
                .Select(m => m.ProjectId)
                .Distinct()
                .ToListAsync(ct);

        if (projectIds.Count == 0)
            return Result.Ok(new ListMyProjectsDto(0, 0, 0, 0, [], []));

        var projects = await db.Projects.IgnoreQueryFilters()
            .Where(p => projectIds.Contains(p.Id))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var tasks = await db.Tasks.IgnoreQueryFilters()
            .Where(t => projectIds.Contains(t.ProjectId))
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

        var names = projects.ToDictionary(p => p.Id, p => p.Name);
        var groups = tasks.GroupBy(t => t.ProjectId).ToDictionary(g => g.Key, g => g.ToList());

        var summaries = projects.Select(p =>
        {
            var list = groups.TryGetValue(p.Id, out var group) ? group : [];
            var lastActivity = list.Aggregate(p.CreatedAt, (acc, t) =>
            {
                var at = t.CompletedAt ?? t.CreatedAt;
                return at > acc ? at : acc;
            });
            return new ProjectSummaryDto(
                p.Id,
                p.Name,
                p.Description,
                p.OrgId,
                list.Count,
                list.Count(t => t.Status == TaskItemStatus.Todo),
                list.Count(t => t.Status == TaskItemStatus.InProgress),
                list.Count(t => t.Status == TaskItemStatus.Done),
                lastActivity);
        }).ToList();

        var recent = tasks
            .Select(t => new RecentTaskDto(
                t.Id,
                t.Title,
                t.Status,
                t.ProjectId,
                names.TryGetValue(t.ProjectId, out var name) ? name : string.Empty,
                t.CompletedAt ?? t.CreatedAt))
            .OrderByDescending(r => r.ActivityAt)
            .Take(RecentLimit)
            .ToList();

        return Result.Ok(new ListMyProjectsDto(
            projects.Count,
            tasks.Count,
            tasks.Count(t => t.Status == TaskItemStatus.InProgress),
            tasks.Count(t => t.Status == TaskItemStatus.Done),
            summaries,
            recent));
    }
}

public record ProjectOverviewDto(
    Guid Id,
    string Name,
    string? Description,
    int TotalTasks,
    int Todo,
    int InProgress,
    int Done,
    DateTime LastActivity,
    IReadOnlyList<RecentTaskDto> RecentTasks);

public class GetProjectOverviewHandler
{
    private const int RecentLimit = 10;

    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetProjectOverviewHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectOverviewDto?>> HandleAsync(
        Guid userId, bool isAdmin, Guid projectId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var isMember = isAdmin
            || await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (!isMember)
            return Result.Ok<ProjectOverviewDto?>(null);

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return Result.Ok<ProjectOverviewDto?>(null);

        var tasks = await db.Tasks.IgnoreQueryFilters()
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(ct);

        var recent = tasks
            .Select(t => new RecentTaskDto(
                t.Id,
                t.Title,
                t.Status,
                t.ProjectId,
                project.Name,
                t.CompletedAt ?? t.CreatedAt))
            .OrderByDescending(t => t.ActivityAt)
            .Take(RecentLimit)
            .ToList();

        var lastActivity = tasks.Aggregate(project.CreatedAt, (acc, t) =>
        {
            var at = t.CompletedAt ?? t.CreatedAt;
            return at > acc ? at : acc;
        });

        return Result.Ok(new ProjectOverviewDto(
            project.Id,
            project.Name,
            project.Description,
            tasks.Count,
            tasks.Count(t => t.Status == TaskItemStatus.Todo),
            tasks.Count(t => t.Status == TaskItemStatus.InProgress),
            tasks.Count(t => t.Status == TaskItemStatus.Done),
            lastActivity,
            recent));
    }
}

public record LabelDto(Guid Id, string Name, string Color, string PathPrefix, Guid ProjectId);

public record ListLabelsRequest(Guid ProjectId);

public class ListLabelsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListLabelsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<LabelDto>>> HandleAsync(ListLabelsRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var labels = await db.Labels
            .Where(l => l.ProjectId == request.ProjectId)
            .OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color, l.PathPrefix, l.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<LabelDto>)labels);
    }
}
