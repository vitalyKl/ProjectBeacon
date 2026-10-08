namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

/// <summary>
/// Working tree for one project on one device. A project has no single root.
/// <see cref="ConfigRevision"/> advances when workstation config changes.
/// <see cref="MarkConfigApplied"/> never moves the applied revision backwards or past <see cref="ConfigRevision"/>.
/// </summary>
public class ProjectRuntime : Entity, IProjectScoped
{
    public ProjectRuntime() { }

    public Guid ProjectId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string LocalRoot { get; private set; } = string.Empty;
    public long ConfigRevision { get; private set; }
    public long AppliedConfigRevision { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public static ProjectRuntime Create(Guid projectId, Guid deviceId, string localRoot)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (deviceId == Guid.Empty)
            throw new ArgumentException("DeviceId is required.", nameof(deviceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);

        var runtime = Entity.New<ProjectRuntime>();
        runtime.ProjectId = projectId;
        runtime.DeviceId = deviceId;
        runtime.LocalRoot = localRoot.Trim();
        runtime.CreatedAt = DateTime.UtcNow;
        return runtime;
    }

    public void SetLocalRoot(string localRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        LocalRoot = localRoot.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void BumpConfig() => ConfigRevision++;

    public void MarkConfigApplied(long revision)
    {
        if (revision < 0)
            return;
        if (revision > ConfigRevision)
            revision = ConfigRevision;
        if (revision > AppliedConfigRevision)
            AppliedConfigRevision = revision;
    }
}
