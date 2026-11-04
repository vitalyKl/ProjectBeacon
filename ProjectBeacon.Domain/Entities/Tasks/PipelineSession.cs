namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// One planner, actor, or review turn. Created Ready. <see cref="Launch"/> requires Ready.
/// <see cref="Close"/> and <see cref="Fail"/> require Ready or Active.
/// </summary>
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
    /// <summary>Latest bounded activity snapshot reported by the workstation while this session runs.</summary>
    public string ActivityJson { get; private set; } = "{}";
    public DateTime? LaunchedAt { get; private set; }
    public DateTime? LastActivityAt { get; private set; }
    public string? FailureReason { get; private set; }
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
        var now = DateTime.UtcNow;
        LaunchedAt = now;
        LastActivityAt = now;
    }

    public void UpdateActivity(string activityJson, bool waitingForUser)
    {
        if (Status is not (SessionStatus.Active or SessionStatus.WaitingForUser))
            throw new InvalidOperationException($"Cannot update activity for session in status {Status}");
        if (string.IsNullOrWhiteSpace(activityJson) || activityJson.Length > 262144)
            throw new ArgumentException("Activity snapshot must be a non-empty JSON payload of at most 256 KiB.", nameof(activityJson));
        ActivityJson = activityJson;
        LastActivityAt = DateTime.UtcNow;
        FailureReason = null;
        Status = waitingForUser ? SessionStatus.WaitingForUser : SessionStatus.Active;
    }

    public void Close()
    {
        if (Status is not (SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
            throw new InvalidOperationException($"Cannot close session in status {Status}");
        Status = SessionStatus.Closed;
        ClosedAt = DateTime.UtcNow;
    }

    public void Fail(string? reason = null)
    {
        if (Status is not (SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
            throw new InvalidOperationException($"Cannot fail session in status {Status}");
        Status = SessionStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Pipeline session failed." : reason.Trim();
        ClosedAt = DateTime.UtcNow;
        LastActivityAt = ClosedAt;
    }
}
