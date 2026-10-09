namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Domain;
using Domain.Enums;
/// <summary>
/// A workstation device. Token is the raw bcd_ value only on enroll or rotate. Other reads leave it empty.
/// </summary>
public record DaemonDeviceDto(
    Guid Id,
    string Name,
    Guid UserId,
    string Fingerprint,
    string TokenPrefix,
    string? Token,
    DateTime? LastHeartbeatAt,
    bool Online,
    string ProbeJson,
    string WorkstationJson,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    long DesiredRevision = 0,
    long AppliedRevision = 0);
/// <summary>
/// A queued device command, its kind, status, payload, and result or error.
/// </summary>
public record WorkstationCommandDto(
    Guid Id,
    Guid DeviceId,
    Guid? ProjectId,
    Guid? RequestedByUserId,
    WorkstationCommandKind Kind,
    WorkstationCommandStatus Status,
    string PayloadJson,
    string? ResultJson,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? LocalRoot = null,
    int Version = CommandProtocol.CurrentVersion);
/// <summary>
/// Runtime summary for any project member: the selected project device and status, without the local path.
/// </summary>
public record RuntimeSummary(
    Guid Id,
    Guid ProjectId,
    Guid DeviceId,
    string DeviceName,
    bool DeviceOnline,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
/// <summary>
/// Runtime diagnostics for privileged viewers (owner/admin, system admin, or device owner): adds the local root.
/// </summary>
public record RuntimeDiagnostics(
    Guid Id,
    Guid ProjectId,
    Guid DeviceId,
    string DeviceName,
    bool DeviceOnline,
    string LocalRoot,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
/// <summary>
/// Name, fingerprint, and owning user. TotpCode is required when that user has TOTP enabled.
/// </summary>
public record CreateDeviceRequest(string Name, string Fingerprint, Guid UserId, string? TotpCode = null);
/// <summary>
/// Command for create device.
/// </summary>
public record CreateDeviceCommand(CreateDeviceRequest Request) : ICommand<Result<DaemonDeviceDto>>;
/// <summary>
/// Fields for list devices.
/// </summary>
public record ListDevicesRequest(Guid UserId);
/// <summary>
/// Command for list devices.
/// </summary>
public record ListDevicesCommand(ListDevicesRequest Request) : ICommand<Result<IList<DaemonDeviceDto>>>;
/// <summary>
/// Fields for revoke device.
/// </summary>
public record RevokeDeviceRequest(Guid DeviceId, Guid UserId);
/// <summary>
/// Command for revoke device.
/// </summary>
public record RevokeDeviceCommand(RevokeDeviceRequest Request) : ICommand<Result>;
/// <summary>
/// Fields for heartbeat device.
/// </summary>
public record HeartbeatDeviceRequest(Guid DeviceId, string? ProbeJson, string? WorkstationJson);
/// <summary>
/// Command for heartbeat device.
/// </summary>
public record HeartbeatDeviceCommand(HeartbeatDeviceRequest Request) : ICommand<Result<DaemonDeviceDto>>;
/// <summary>
/// Device, caller, command kind, optional JSON payload, and optional project.
/// </summary>
public record EnqueueCommandRequest(
    Guid DeviceId,
    Guid UserId,
    WorkstationCommandKind Kind,
    string? PayloadJson,
    Guid? ProjectId);
/// <summary>
/// Command for enqueue command.
/// </summary>
public record EnqueueCommandCommand(EnqueueCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;
/// <summary>
/// Device claiming its next command. Wait is how long the claim may block.
/// </summary>
public record ClaimNextCommandRequest(Guid DeviceId, TimeSpan? Wait);
/// <summary>
/// Command for claim next command.
/// </summary>
public record ClaimNextCommandCommand(ClaimNextCommandRequest Request) : ICommand<Result<WorkstationCommandDto?>>;
/// <summary>
/// Device finishing a command: success, optional result JSON, and optional error.
/// </summary>
public record CompleteCommandRequest(Guid CommandId, Guid DeviceId, bool Success, string? ResultJson, string? Error);
/// <summary>
/// Command for complete command.
/// </summary>
public record CompleteCommandCommand(CompleteCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;
/// <summary>
/// Fields for get command.
/// </summary>
public record GetCommandRequest(Guid CommandId, Guid UserId);
/// <summary>
/// Command for get command.
/// </summary>
public record GetCommandCommand(GetCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;
/// <summary>
/// Fields for list commands.
/// </summary>
public record ListCommandsRequest(Guid DeviceId, Guid UserId, int Limit = 20);
/// <summary>
/// Command for list commands.
/// </summary>
public record ListCommandsCommand(ListCommandsRequest Request) : ICommand<Result<IList<WorkstationCommandDto>>>;
/// <summary>
/// Project, device, user, and the local root to bind.
/// </summary>
public record AttachRuntimeRequest(Guid ProjectId, Guid DeviceId, Guid UserId, string LocalRoot);
/// <summary>
/// Command for attach runtime.
/// </summary>
public record AttachRuntimeCommand(AttachRuntimeRequest Request) : ICommand<Result<RuntimeDiagnostics>>;
/// <summary>
/// Fields for list runtimes.
/// </summary>
public record ListRuntimesRequest(Guid ProjectId, Guid UserId);
/// <summary>
/// Command for list runtimes.
/// </summary>
public record ListRuntimesCommand(ListRuntimesRequest Request) : ICommand<Result<IList<RuntimeSummary>>>;
/// <summary>
/// User and runtime id for detach runtime.
/// </summary>
public record DetachRuntimeRequest(Guid RuntimeId, Guid UserId);
/// <summary>
/// Command for detach runtime.
/// </summary>
public record DetachRuntimeCommand(DetachRuntimeRequest Request) : ICommand<Result>;
/// <summary>
/// Runtime id and requesting user for runtime diagnostics.
/// </summary>
public record GetRuntimeDiagnosticsRequest(Guid RuntimeId, Guid UserId);
/// <summary>
/// Command for get runtime diagnostics.
/// </summary>
public record GetRuntimeDiagnosticsCommand(GetRuntimeDiagnosticsRequest Request) : ICommand<Result<RuntimeDiagnostics>>;
/// <summary>
/// Generated llama-swap YAML and the port the client should use.
/// </summary>
public record LlamaSwapConfigDto(string Yaml, int Port);
/// <summary>
/// Fields for get llama swap config.
/// </summary>
public record GetLlamaSwapConfigRequest(Guid DeviceId);
/// <summary>
/// Command for get llama swap config.
/// </summary>
public record GetLlamaSwapConfigCommand(GetLlamaSwapConfigRequest Request) : ICommand<Result<LlamaSwapConfigDto>>;
/// <summary>
/// One CPU, memory, and GPU sample from a device heartbeat.
/// </summary>
public record DeviceHostSampleDto(
    Guid Id,
    Guid DeviceId,
    DateTime SampledAt,
    double? CpuPercent,
    long? RamUsedBytes,
    long? RamTotalBytes,
    string? GpuName,
    double? GpuUtilizationPercent,
    long? GpuMemoryUsedBytes,
    long? GpuMemoryTotalBytes);
/// <summary>
/// User whose samples to list. DeviceId limits the list to one device.
/// </summary>
public record ListHostSamplesRequest(Guid UserId, Guid? DeviceId = null);
/// <summary>
/// Command for list host samples.
/// </summary>
public record ListHostSamplesCommand(ListHostSamplesRequest Request) : ICommand<Result<IList<DeviceHostSampleDto>>>;
