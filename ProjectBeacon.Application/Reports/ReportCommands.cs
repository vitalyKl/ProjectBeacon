namespace ProjectBeacon.Application.Reports;

using System.Text.Json;
using Application.Common;
using Application.Authorization;
using Domain.Entities.Projects;
using Domain.Entities.Reports;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Open and closed milestone counts stored in a report snapshot.
/// </summary>
public record ReportMilestoneCounts(int Open, int Closed);
/// <summary>
/// Board counts at the time a report was generated, including ready and in-flight task ids.
/// </summary>
public record ReportSnapshot(
    DateTime GeneratedAt,
    ReportMilestoneCounts Milestones,
    IReadOnlyDictionary<string, int> Tasks,
    IReadOnlyList<Guid> ReadyTaskIds,
    IReadOnlyList<Guid> InFlightTaskIds);
/// <summary>
/// A saved report: markdown, the snapshot JSON, who created it, and when.
/// </summary>
public record ReportDto(
    Guid Id,
    string Title,
    string BodyMarkdown,
    string SnapshotJson,
    string CreatedByType,
    string CreatedById,
    DateTime CreatedAt,
    Guid ProjectId);
/// <summary>
/// Project and the creator type and id to stamp on a new board snapshot.
/// </summary>
public record GenerateReportRequest(Guid ProjectId);
/// <summary>
/// Command for generate report.
/// </summary>
public record GenerateReportCommand(GenerateReportRequest Request, ActorContext Actor) : ICommand<Result<ReportDto>>;
/// <summary>
/// Fields for list reports.
/// </summary>
public record ListReportsRequest(Guid ProjectId, ActorContext Actor);
/// <summary>
/// Fields for get report.
/// </summary>
public record GetReportRequest(Guid ProjectId, Guid ReportId, ActorContext Actor);
/// <summary>
/// Stores a board snapshot report for a project.
/// </summary>
public class GenerateReportHandler : ICommandHandler<GenerateReportCommand, Result<ReportDto>>
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GenerateReportHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ReportDto>> HandleAsync(GenerateReportCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is not { } creatorId || creatorId == Guid.Empty)
            return Result.Forbidden<ReportDto>();

        await using var db = _dbFactory.CreateDbContext();
        var access = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Report, AuthAction.Create,
            projectId: command.Request.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<ReportDto>();

        var project = await db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ReportDto>("Project not found.", ErrorKind.NotFound);

        var tasks = await db.Tasks
            .Where(t => t.ProjectId == command.Request.ProjectId)
            .ToListAsync(ct);
        var milestones = await db.Milestones
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .ToListAsync(ct);

        var counts = new Dictionary<string, int>
        {
            [nameof(TaskItemStatus.Todo)] = tasks.Count(t => t.Status == TaskItemStatus.Todo),
            [nameof(TaskItemStatus.InProgress)] = tasks.Count(t => t.Status == TaskItemStatus.InProgress),
            [nameof(TaskItemStatus.Done)] = tasks.Count(t => t.Status == TaskItemStatus.Done)
        };
        var ready = tasks.Where(t => t.Status == TaskItemStatus.Todo).Select(t => t.Id).ToList();
        var inFlight = tasks.Where(t => t.Status == TaskItemStatus.InProgress).Select(t => t.Id).ToList();
        var now = DateTime.UtcNow;
        var snapshot = new ReportSnapshot(
            now,
            new ReportMilestoneCounts(
                milestones.Count(m => m.ClosedAt is null),
                milestones.Count(m => m.ClosedAt is not null)),
            counts,
            ready,
            inFlight);

        var body = BuildMarkdown(project.Name, snapshot, tasks);
        var title = $"{project.Name} development report";
        // Attribution is security metadata. It always comes from the authenticated
        // server-side actor; the request contract contains no creator fields.
        var report = Report.Create(
            title,
            body,
            JsonSerializer.Serialize(snapshot, Json),
            project.Id,
            "user",
            creatorId.ToString("D"));
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(report));
    }

    internal static string BuildMarkdown(string projectName, ReportSnapshot snapshot, IReadOnlyList<TaskItem> tasks)
    {
        var byId = tasks.ToDictionary(t => t.Id);
        var lines = new List<string>
        {
            $"# {projectName} development report",
            "",
            $"Generated {snapshot.GeneratedAt:O}.",
            "",
            "## Snapshot",
            "",
            $"- Open milestones: {snapshot.Milestones.Open}",
            $"- Closed milestones: {snapshot.Milestones.Closed}",
            $"- Todo: {snapshot.Tasks.GetValueOrDefault(nameof(TaskItemStatus.Todo), 0)}",
            $"- In progress: {snapshot.Tasks.GetValueOrDefault(nameof(TaskItemStatus.InProgress), 0)}",
            $"- Done: {snapshot.Tasks.GetValueOrDefault(nameof(TaskItemStatus.Done), 0)}",
            ""
        };
        if (snapshot.ReadyTaskIds.Count > 0)
        {
            lines.Add("## Ready for agents");
            lines.Add("");
            foreach (var id in snapshot.ReadyTaskIds)
                lines.Add($"- {byId.GetValueOrDefault(id)?.Title ?? id.ToString()}");
            lines.Add("");
        }

        if (snapshot.InFlightTaskIds.Count > 0)
        {
            lines.Add("## In flight");
            lines.Add("");
            foreach (var id in snapshot.InFlightTaskIds)
                lines.Add($"- {byId.GetValueOrDefault(id)?.Title ?? id.ToString()}");
            lines.Add("");
        }

        return string.Join('\n', lines);
    }

    internal static ReportDto Map(Report report) =>
        new(report.Id, report.Title, report.BodyMarkdown, report.SnapshotJson,
            report.CreatedByType, report.CreatedById, report.CreatedAt, report.ProjectId);
}
/// <summary>
/// Lists reports for a project.
/// </summary>
public class ListReportsHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListReportsHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<IList<ReportDto>>> HandleAsync(ListReportsRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var access = await _authorization.CanAsync(
            db, request.Actor, ResourceType.Report, AuthAction.Read,
            projectId: request.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<IList<ReportDto>>();

        var items = await db.Reports
            .Where(r => r.ProjectId == request.ProjectId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReportDto(
                r.Id, r.Title, r.BodyMarkdown, r.SnapshotJson,
                r.CreatedByType, r.CreatedById, r.CreatedAt, r.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<ReportDto>)items);
    }
}
/// <summary>
/// Loads one report.
/// </summary>
public class GetReportHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetReportHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ReportDto>> HandleAsync(GetReportRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var access = await _authorization.CanAsync(
            db, request.Actor, ResourceType.Report, AuthAction.Read,
            projectId: request.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<ReportDto>();

        var report = await db.Reports
            .FirstOrDefaultAsync(r => r.Id == request.ReportId && r.ProjectId == request.ProjectId, ct);
        if (report is null)
            return Result.Failure<ReportDto>("Report not found.");
        return Result.Ok(GenerateReportHandler.Map(report));
    }
}