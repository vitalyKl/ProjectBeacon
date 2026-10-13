namespace ProjectBeacon.Domain.Entities.Devices;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A command queued for a <see cref="DaemonDevice"/>. Not project-scoped; <see cref="ProjectId"/> is optional context.
/// Created as <see cref="WorkstationCommandStatus.Pending"/>.
/// </summary>
public class WorkstationCommand : Entity
{
    public WorkstationCommand() { }

    public Guid DeviceId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public WorkstationCommandKind Kind { get; private set; }
    public WorkstationCommandStatus Status { get; private set; }
    public int Version { get; private set; } = CommandProtocol.CurrentVersion;
    public string PayloadJson { get; private set; } = "{}";
    public string? ResultJson { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Number of times the recovery rule has re-queued this command. The current attempt is <c>Attempts + 1</c>.</summary>
    public int Attempts { get; private set; }
    /// <summary>End of the execution lease granted at claim. Cleared on re-queue or terminal transition.</summary>
    public DateTime? LeaseExpiresAt { get; private set; }
    /// <summary>Daemon process instance id (from the device heartbeat) that claimed the command, when known.</summary>
    public string? ClaimInstanceId { get; private set; }

    /// <summary>
    /// Queues a pending command for <paramref name="deviceId"/>. Blank payload is stored as <c>{}</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> is empty.</exception>
    public static WorkstationCommand Create(
        Guid deviceId,
        WorkstationCommandKind kind,
        string? payloadJson = null,
        Guid? projectId = null,
        Guid? requestedByUserId = null)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("DeviceId is required.", nameof(deviceId));

        var command = Entity.New<WorkstationCommand>();
        command.DeviceId = deviceId;
        command.Kind = kind;
        command.Status = WorkstationCommandStatus.Pending;
        command.Version = CommandProtocol.CurrentVersion;
        command.PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        command.ProjectId = projectId;
        command.RequestedByUserId = requestedByUserId;
        command.CreatedAt = DateTime.UtcNow;
        return command;
    }

    /// <summary>
    /// Moves a pending command to <see cref="WorkstationCommandStatus.Running"/> and grants
    /// <paramref name="lease"/> of execution time.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command is not pending.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lease"/> is not positive.</exception>
    public void Claim(TimeSpan lease, string? instanceId = null)
    {
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "Lease must be positive.");
        if (Status != WorkstationCommandStatus.Pending)
            throw new InvalidOperationException("Command is not pending.");
        Status = WorkstationCommandStatus.Running;
        StartedAt = DateTime.UtcNow;
        LeaseExpiresAt = StartedAt + lease;
        ClaimInstanceId = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();
    }

    /// <summary>
    /// Returns an expired running command to the queue. The recovery rule must have
    /// established the claiming process is gone before calling this.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command is not running.</exception>
    public void Requeue()
    {
        if (Status != WorkstationCommandStatus.Running)
            throw new InvalidOperationException("Command is not running.");
        Status = WorkstationCommandStatus.Pending;
        Attempts++;
        StartedAt = null;
        Error = null;
        LeaseExpiresAt = null;
    }

    /// <summary>
    /// Marks the command succeeded and stores <paramref name="resultJson"/>. Allowed from pending or running.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command is already succeeded, failed, or cancelled.</exception>
    public void Succeed(string? resultJson)
    {
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Succeeded;
        ResultJson = resultJson;
        Error = null;
        CompletedAt = DateTime.UtcNow;
        StartedAt ??= CompletedAt;
        LeaseExpiresAt = null;
    }

    /// <summary>
    /// Marks the command failed. Allowed from pending or running.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="error"/> is null or white space.</exception>
    /// <exception cref="InvalidOperationException">The command is already succeeded, failed, or cancelled.</exception>
    public void Fail(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Failed;
        Error = error;
        CompletedAt = DateTime.UtcNow;
        StartedAt ??= CompletedAt;
        LeaseExpiresAt = null;
    }

    /// <summary>
    /// Marks the command cancelled. Allowed from pending or running.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command is already succeeded, failed, or cancelled.</exception>
    public void Cancel()
    {
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }
}
