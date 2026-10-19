namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a project in an org and seeds the starter labels. The caller becomes owner.
/// The caller must be an org Owner/Admin or a system admin. The rights check and the
/// inserts run on a separate tenant instance keyed by the target org and the new project
/// id, so the flow works for callers that hold no tenant without ever mutating the
/// circuit's scoped tenant (concurrent operations on the same circuit are unaffected).
/// </summary>
public class CreateProjectHandler : ICommandHandler<CreateProjectCommand, Result<ProjectDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService? _auth;
    private readonly ITenantContext _tenant;

    public CreateProjectHandler(IBeaconDbFactory dbFactory, IAuthorizationService? auth, ITenantContext tenant)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _tenant = tenant;
    }

    public async Task<Result<ProjectDto>> HandleAsync(CreateProjectCommand command, CancellationToken ct = default)
    {
        var orgId = command.Request.OrgId;

        await using var db = _dbFactory.CreateDbContext();
        var orgExists = await db.Orgs.AnyAsync(o => o.Id == orgId, ct);
        if (!orgExists)
            return Result.Failure<ProjectDto>("Org not found.");

        if (command.Actor.UserId is { } userId
            && !await db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<ProjectDto>("User not found.");

        var window = new TenantContext();
        window.Assign(_tenant.ProjectId, orgId, _tenant.Unscoped);
        await using var authDb = _dbFactory.CreateDbContext(window);
        if (!await CanCreateInOrgAsync(authDb, command.Actor, orgId, ct))
            return Result.Forbidden<ProjectDto>();

        var project = Domain.Entities.Projects.Project.Create(command.Request.Name, command.Request.Description, orgId);

        window.Assign(project.Id, orgId, _tenant.Unscoped);
        await using var writeDb = _dbFactory.CreateDbContext(window);
        writeDb.Projects.Add(project);
        if (command.Actor.UserId is { } ownerId)
            writeDb.ProjectMembers.Add(ProjectMember.Create(project.Id, ownerId, MemberRole.Owner));
        SeedStarterLabels(writeDb, project.Id);
        await writeDb.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    // The membership rows are tenant-scoped, so the check must run inside the RLS window
    // for the target org. Delegates to IAuthorizationService when one was injected; a
    // direct construction without it (e.g. tests) applies the same rule: org Owner/Admin
    // or system admin.
    private async Task<bool> CanCreateInOrgAsync(IBeaconDb db, ActorContext actor, Guid orgId, CancellationToken ct)
    {
        if (actor.UserId is not { } userId)
            return false;

        if (_auth is not null)
            return (await _auth.CanAsync(db, actor, ResourceType.Org, AuthAction.Administer, orgId: orgId, ct: ct)).Success;

        if (await db.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.OrgId == orgId && m.UserId == userId
                    && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct))
            return true;
        return await db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Id == userId && u.IsAdmin, ct);
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt, project.ReviewCheckCommand);

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
/// <summary>
/// Renames a project. Requires project administer. An empty name fails.
/// </summary>
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

        project.Update(name, command.Request.Description, command.Request.ReviewCheckCommand);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt, project.ReviewCheckCommand);
}
/// <summary>
/// Loads one project in the current tenant.
/// </summary>
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
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt, project.ReviewCheckCommand);
}
/// <summary>
/// Lists projects in the current tenant, optionally for one org.
/// </summary>
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
                p.Id, p.Name, p.Description, p.OrgId, p.CreatedAt, p.UpdatedAt, p.ReviewCheckCommand))
            .ToListAsync(ct);

        return Result.Ok((IList<ProjectDto>)projects);
    }
}
/// <summary>
/// Deletes a project and its dependent rows. Requires project administer. A database conflict returns a failure instead of throwing.
/// </summary>
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