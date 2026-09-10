namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateProjectHandler : ICommandHandler<CreateProjectCommand, Result<ProjectDto>>
{
    private readonly BeaconDbContext _db;

    public CreateProjectHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ProjectDto>> HandleAsync(CreateProjectCommand command, CancellationToken ct = default)
    {
        var orgExists = await _db.Orgs.AnyAsync(o => o.Id == command.Request.OrgId, ct);
        if (!orgExists)
            return Result.Failure<ProjectDto>("Org not found.");

        if (command.Request.CreatedByUserId is { } userId
            && !await _db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<ProjectDto>("User not found.");

        var project = Domain.Entities.Projects.Project.Create(command.Request.Name, command.Request.Description, command.Request.OrgId);

        _db.Projects.Add(project);
        if (command.Request.CreatedByUserId is { } ownerId)
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, ownerId, MemberRole.Owner));
        SeedStarterLabels(project.Id);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);

    private void SeedStarterLabels(Guid projectId)
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
            _db.Labels.Add(Label.Create(name, color, projectId, prefix));
    }
}

public class UpdateProjectHandler : ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>
{
    private readonly BeaconDbContext _db;

    public UpdateProjectHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ProjectDto>> HandleAsync(UpdateProjectCommand command, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ProjectDto>("Project not found.");

        project.Update(command.Request.Name, command.Request.Description);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);
}

public class GetProjectHandler : ICommandHandler<GetProjectCommand, Result<ProjectDto>>
{
    private readonly BeaconDbContext _db;

    public GetProjectHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ProjectDto>> HandleAsync(GetProjectCommand command, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ProjectDto>("Project not found.");

        return Result.Ok(MapToDto(project));
    }

    private static ProjectDto MapToDto(Domain.Entities.Projects.Project project) =>
        new(project.Id, project.Name, project.Description, project.OrgId, project.CreatedAt, project.UpdatedAt);
}

public class ListProjectsHandler : ICommandHandler<ListProjectsCommand, Result<IList<ProjectDto>>>
{
    private readonly BeaconDbContext _db;

    public ListProjectsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<ProjectDto>>> HandleAsync(ListProjectsCommand command, CancellationToken ct = default)
    {
        var query = _db.Projects.AsQueryable();
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
