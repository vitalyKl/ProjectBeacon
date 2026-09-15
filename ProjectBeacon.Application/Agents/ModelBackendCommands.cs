namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Domain.Enums;
using Infrastructure.LlamaSwap;

public record LocalModelBackendDto(
    Guid Id,
    string Name,
    ModelBackendType BackendType,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    IReadOnlyList<string> ExtraFlags,
    Guid ProjectId,
    DateTime? UpdatedAt);

public record RoleBindingDto(
    Guid Id,
    PipelineRole Role,
    Guid ModelBackendId,
    Guid ProjectId);

public record ModelRegistryDto(
    Guid ProjectId,
    IReadOnlyList<LocalModelBackendDto> Backends,
    IReadOnlyList<RoleBindingDto> Bindings);

public record UpsertLocalModelBackendRequest(
    Guid? Id,
    string Name,
    ModelBackendType BackendType,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    IReadOnlyList<string>? ExtraFlags = null);

public record UpsertLocalModelBackendCommand(UpsertLocalModelBackendRequest Request) : ICommand<Result<LocalModelBackendDto>>;

public record DeleteLocalModelBackendRequest(Guid Id);

public record DeleteLocalModelBackendCommand(DeleteLocalModelBackendRequest Request) : ICommand<Result<bool>>;

public record SetRoleBindingRequest(PipelineRole Role, Guid ModelBackendId);

public record SetRoleBindingCommand(SetRoleBindingRequest Request) : ICommand<Result<RoleBindingDto>>;

public record RemoveRoleBindingRequest(PipelineRole Role);

public record RemoveRoleBindingCommand(RemoveRoleBindingRequest Request) : ICommand<Result<bool>>;

public record GetModelRegistryCommand : ICommand<Result<ModelRegistryDto>>;

public record GetProxyStatusCommand : ICommand<Result<LlamaSwapStatusDto>>;

public record ReloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;

public record UnloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;
