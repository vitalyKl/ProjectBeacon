namespace ProjectBeacon.Domain.Entities.Devices;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class WorkstationCommand : Entity
{
    public WorkstationCommand() { }

    public Guid DeviceId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public WorkstationCommandKind Kind { get; private set; }
    public WorkstationCommandStatus Status { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public string? ResultJson { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

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
        command.PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        command.ProjectId = projectId;
        command.RequestedByUserId = requestedByUserId;
        command.CreatedAt = DateTime.UtcNow;
        return command;
    }

    public void Claim()
    {
        if (Status != WorkstationCommandStatus.Pending)
            throw new InvalidOperationException("Command is not pending.");
        Status = WorkstationCommandStatus.Running;
        StartedAt = DateTime.UtcNow;
    }

    public void Succeed(string? resultJson)
    {
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Succeeded;
        ResultJson = resultJson;
        Error = null;
        CompletedAt = DateTime.UtcNow;
        StartedAt ??= CompletedAt;
    }

    public void Fail(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Failed;
        Error = error;
        CompletedAt = DateTime.UtcNow;
        StartedAt ??= CompletedAt;
    }

    public void Cancel()
    {
        if (Status is WorkstationCommandStatus.Succeeded or WorkstationCommandStatus.Failed or WorkstationCommandStatus.Cancelled)
            throw new InvalidOperationException("Command already finished.");
        Status = WorkstationCommandStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }
}
