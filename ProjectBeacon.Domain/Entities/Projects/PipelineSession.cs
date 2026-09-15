namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class PipelineSession : Entity, IProjectScoped
{
    public PipelineSession() { }

    public Guid TaskId { get; private set; }
    public Guid? SubtaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public PipelineRole Role { get; private set; }
    public SessionStatus Status { get; private set; }
    public string? ExternalSessionId { get; private set; }
    public Guid? ModelBackendId { get; private set; }
    public string? LaunchSpec { get; private set; }
    public string PromptContext { get; private set; } = string.Empty;
    public DateTime? LaunchedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    public TaskItem Task { get; private set; } = null!;

    public static PipelineSession Create(
        Guid taskId,
        Guid projectId,
        PipelineRole role,
        string promptContext,
        Guid? subtaskId = null,
        Guid? modelBackendId = null,
        string? launchSpec = null)
    {
        if (string.IsNullOrWhiteSpace(promptContext))
            throw new ArgumentException("Prompt context cannot be empty.", nameof(promptContext));

        var session = Entity.New<PipelineSession>();
        session.TaskId = taskId;
        session.ProjectId = projectId;
        session.Role = role;
        session.PromptContext = promptContext;
        session.SubtaskId = subtaskId;
        session.ModelBackendId = modelBackendId;
        session.LaunchSpec = launchSpec;
        session.Status = SessionStatus.Ready;
        return session;
    }

    public void Launch(string? externalSessionId = null)
    {
        if (Status != SessionStatus.Ready)
            throw new InvalidOperationException($"Cannot launch session in status {Status}");
        Status = SessionStatus.Active;
        if (externalSessionId is not null)
            ExternalSessionId = externalSessionId;
        LaunchedAt = DateTime.UtcNow;
    }

    public void Close()
    {
        if (Status is not (SessionStatus.Ready or SessionStatus.Active))
            throw new InvalidOperationException($"Cannot close session in status {Status}");
        Status = SessionStatus.Closed;
        ClosedAt = DateTime.UtcNow;
    }

    public void Fail()
    {
        if (Status is not (SessionStatus.Ready or SessionStatus.Active))
            throw new InvalidOperationException($"Cannot fail session in status {Status}");
        Status = SessionStatus.Failed;
        ClosedAt = DateTime.UtcNow;
    }
}
