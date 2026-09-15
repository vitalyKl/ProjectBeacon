namespace ProjectBeacon.Application.Reports;

using System.Text.Json;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ReportMilestoneCounts(int Open, int Closed);

public record ReportSnapshot(
    DateTime GeneratedAt,
    ReportMilestoneCounts Milestones,
    IReadOnlyDictionary<string, int> Tasks,
    IReadOnlyList<Guid> ReadyTaskIds,
    IReadOnlyList<Guid> InFlightTaskIds);

public record ReportDto(
    Guid Id,
    string Title,
    string BodyMarkdown,
    string SnapshotJson,
    string CreatedByType,
    string CreatedById,
    DateTime CreatedAt,
    Guid ProjectId);

public record GenerateReportRequest(Guid ProjectId, string CreatedByType, string CreatedById);

public record GenerateReportCommand(GenerateReportRequest Request) : ICommand<Result<ReportDto>>;

public record ListReportsRequest(Guid ProjectId);

public record GetReportRequest(Guid ProjectId, Guid ReportId);

public class GenerateReportHandler : ICommandHandler<GenerateReportCommand, Result<ReportDto>>
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GenerateReportHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ReportDto>> HandleAsync(GenerateReportCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var project = await db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ReportDto>("Project not found.");

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
        var createdByType = string.IsNullOrWhiteSpace(command.Request.CreatedByType)
            ? "user"
            : command.Request.CreatedByType.Trim();
        var createdById = command.Request.CreatedById ?? string.Empty;

        var report = Report.Create(
            title,
            body,
            JsonSerializer.Serialize(snapshot, Json),
            project.Id,
            createdByType,
            createdById);
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

public class ListReportsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListReportsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ReportDto>>> HandleAsync(ListReportsRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
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

public class GetReportHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetReportHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ReportDto>> HandleAsync(GetReportRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var report = await db.Reports
            .FirstOrDefaultAsync(r => r.Id == request.ReportId && r.ProjectId == request.ProjectId, ct);
        if (report is null)
            return Result.Failure<ReportDto>("Report not found.");
        return Result.Ok(GenerateReportHandler.Map(report));
    }
}