namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class ProjectRuntime : Entity, IProjectScoped
{
    public ProjectRuntime() { }

    public Guid ProjectId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string LocalRoot { get; private set; } = string.Empty;
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
}
