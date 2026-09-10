namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class Report : Entity, IProjectScoped
{
    public Report() { }

    public string Title { get; private set; } = string.Empty;
    public string BodyMarkdown { get; private set; } = string.Empty;
    public string SnapshotJson { get; private set; } = "{}";
    public string CreatedByType { get; private set; } = "user";
    public string CreatedById { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public Guid ProjectId { get; private set; }

    public Project Project { get; private set; } = null!;

    public static Report Create(
        string title,
        string bodyMarkdown,
        string snapshotJson,
        Guid projectId,
        string createdByType,
        string createdById)
    {
        var report = Entity.New<Report>();
        report.Title = title;
        report.BodyMarkdown = bodyMarkdown;
        report.SnapshotJson = snapshotJson;
        report.ProjectId = projectId;
        report.CreatedByType = createdByType;
        report.CreatedById = createdById;
        report.CreatedAt = DateTime.UtcNow;
        return report;
    }
}
