namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Domain.Enums;

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
    DateTime CreatedAt);

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
    DateTime? CompletedAt);

public record ProjectRuntimeDto(
    Guid Id,
    Guid ProjectId,
    Guid DeviceId,
    string DeviceName,
    bool DeviceOnline,
    string LocalRoot,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record CreateDeviceRequest(string Name, string Fingerprint, Guid UserId);
public record CreateDeviceCommand(CreateDeviceRequest Request) : ICommand<Result<DaemonDeviceDto>>;

public record ListDevicesRequest(Guid UserId);
public record ListDevicesCommand(ListDevicesRequest Request) : ICommand<Result<IList<DaemonDeviceDto>>>;

public record RevokeDeviceRequest(Guid DeviceId, Guid UserId);
public record RevokeDeviceCommand(RevokeDeviceRequest Request) : ICommand<Result>;

public record HeartbeatDeviceRequest(Guid DeviceId, string? ProbeJson, string? WorkstationJson);
public record HeartbeatDeviceCommand(HeartbeatDeviceRequest Request) : ICommand<Result<DaemonDeviceDto>>;

public record EnqueueCommandRequest(
    Guid DeviceId,
    Guid UserId,
    WorkstationCommandKind Kind,
    string? PayloadJson,
    Guid? ProjectId);
public record EnqueueCommandCommand(EnqueueCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;

public record ClaimNextCommandRequest(Guid DeviceId, TimeSpan? Wait);
public record ClaimNextCommandCommand(ClaimNextCommandRequest Request) : ICommand<Result<WorkstationCommandDto?>>;

public record CompleteCommandRequest(Guid CommandId, Guid DeviceId, bool Success, string? ResultJson, string? Error);
public record CompleteCommandCommand(CompleteCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;

public record GetCommandRequest(Guid CommandId, Guid UserId);
public record GetCommandCommand(GetCommandRequest Request) : ICommand<Result<WorkstationCommandDto>>;

public record AttachRuntimeRequest(Guid ProjectId, Guid DeviceId, Guid UserId, string LocalRoot);
public record AttachRuntimeCommand(AttachRuntimeRequest Request) : ICommand<Result<ProjectRuntimeDto>>;

public record ListRuntimesRequest(Guid ProjectId, Guid UserId);
public record ListRuntimesCommand(ListRuntimesRequest Request) : ICommand<Result<IList<ProjectRuntimeDto>>>;

public record DetachRuntimeRequest(Guid RuntimeId, Guid UserId);
public record DetachRuntimeCommand(DetachRuntimeRequest Request) : ICommand<Result>;

public record LlamaSwapConfigDto(string Yaml, int Port);

public record GetLlamaSwapConfigRequest(Guid DeviceId);
public record GetLlamaSwapConfigCommand(GetLlamaSwapConfigRequest Request) : ICommand<Result<LlamaSwapConfigDto>>;

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

public record ListHostSamplesRequest(Guid UserId, Guid? DeviceId = null);
public record ListHostSamplesCommand(ListHostSamplesRequest Request) : ICommand<Result<IList<DeviceHostSampleDto>>>;
