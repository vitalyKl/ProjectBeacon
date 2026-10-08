namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateProjectHandler : ICommandHandler<CreateProjectCommand, Result<ProjectDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CreateProjectHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectDto>> HandleAsync(CreateProjectCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var orgExists = await db.Orgs.AnyAsync(o => o.Id == command.Request.OrgId, ct);
        if (!orgExists)
            return Result.Failure<ProjectDto>("Org not found.");

        if (command.Actor.UserId is { } userId
            && !await db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<ProjectDto>("User not found.");

        var project = Domain.Entities.Projects.Project.Create(command.Request.Name, command.Request.Description, command.Request.OrgId);

        db.Projects.Add(project);
        if (command.Actor.UserId is { } ownerId)
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, ownerId, MemberRole.Owner));
        SeedStarterLabels(db, project.Id);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);

    private static void SeedStarterLabels(IBeaconDb db, Guid projectId)
    {
        (string Name, string Color, string Prefix)[] catalog =
        [
            ("API", "#3f3f46", "ProjectBeacon.API"),
            ("Web", "#52525b", "ProjectBeacon.Web"),
            ("CLI", "#71717a", "ProjectBeacon.Cli"),
            ("Visual", "#27272a", "ProjectBeacon.Web/wwwroot"),
            ("UX", "#18181b", "ProjectBeacon.Web/Pages")
        ];

        foreach (var (name, color, prefix) in catalog)
            db.Labels.Add(Label.Create(name, color, projectId, prefix));
    }
}

public class UpdateProjectHandler : ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public UpdateProjectHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<ProjectDto>> HandleAsync(UpdateProjectCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: command.Request.ProjectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<ProjectDto>();

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == command.Request.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProjectDto>("Project not found.");

        var name = command.Request.Name;
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0)
                return Result.Failure<ProjectDto>("Project name is required.");
        }

        project.Update(name, command.Request.Description);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);
}

public class GetProjectHandler : ICommandHandler<GetProjectCommand, Result<ProjectDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetProjectHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectDto>> HandleAsync(GetProjectCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var project = await db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ProjectDto>("Project not found.");

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);
}

public class ListProjectsHandler : ICommandHandler<ListProjectsCommand, Result<IList<ProjectDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProjectsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ProjectDto>>> HandleAsync(ListProjectsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var query = db.Projects.AsQueryable();
        if (command.Request.OrgId is { } orgId)
            query = query.Where(p => p.OrgId == orgId);

        var projects = await query
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ProjectDto(
                p.Id, p.Name, p.Description, p.OrgId, p.CreatedAt, p.UpdatedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<ProjectDto>)projects);
    }
}

public class DeleteProjectHandler : ICommandHandler<DeleteProjectCommand, Result<bool>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public DeleteProjectHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<bool>> HandleAsync(DeleteProjectCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: command.ProjectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<bool>();

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<bool>("Project not found.");

        var taskIds = db.Tasks.IgnoreQueryFilters().Where(t => t.ProjectId == command.ProjectId).Select(t => t.Id);
        await db.TaskDependencies.IgnoreQueryFilters()
            .Where(d => taskIds.Contains(d.TaskId) || taskIds.Contains(d.DependentTaskId))
            .ExecuteDeleteAsync(ct);

        await db.Decisions.IgnoreQueryFilters()
            .Where(d => d.ProjectId == command.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.SupersededById, (Guid?)null), ct);
        await db.Decisions.IgnoreQueryFilters()
            .Where(d => d.ProjectId == command.ProjectId)
            .ExecuteDeleteAsync(ct);

        await db.ContextSections.IgnoreQueryFilters()
            .Where(s => s.RepoId == command.ProjectId && s.ProjectId != command.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RepoId, (Guid?)null), ct);
        await db.ContextSections.IgnoreQueryFilters()
            .Where(s => s.ProjectId == command.ProjectId)
            .ExecuteDeleteAsync(ct);
        await db.Constraints.IgnoreQueryFilters()
            .Where(c => c.ProjectId == command.ProjectId)
            .ExecuteDeleteAsync(ct);

        db.Projects.Remove(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<bool>("Project could not be deleted.");
        }

        return Result.Ok(true);
    }
}