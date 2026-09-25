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
    Guid UserId,
    DateTime? UpdatedAt,
    bool Concurrent = false,
    string Note = "");

public record RoleBindingDto(
    Guid Id,
    PipelineRole Role,
    Guid ModelBackendId,
    Guid ProjectId);

public record AgentTemplateDto(
    Guid Id,
    string Name,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);

public record ModelRegistryDto(
    Guid UserId,
    IReadOnlyList<LocalModelBackendDto> Backends,
    IReadOnlyList<RoleBindingDto> Bindings,
    IReadOnlyList<AgentTemplateDto> Templates);

public record UpsertLocalModelBackendRequest(
    Guid? Id,
    string Name,
    ModelBackendType BackendType,
    string LaunchCommand,
    int ContextSize,
    int Ttl,
    Guid UserId,
    IReadOnlyList<string>? ExtraFlags = null,
    bool Concurrent = false,
    string? Note = null);

public record UpsertLocalModelBackendCommand(UpsertLocalModelBackendRequest Request) : ICommand<Result<LocalModelBackendDto>>;

public record DeleteLocalModelBackendRequest(Guid Id, Guid UserId);

public record DeleteLocalModelBackendCommand(DeleteLocalModelBackendRequest Request) : ICommand<Result>;

public record SetRoleBindingRequest(PipelineRole Role, Guid ModelBackendId);

public record SetRoleBindingCommand(SetRoleBindingRequest Request) : ICommand<Result<RoleBindingDto>>;

public record RemoveRoleBindingRequest(PipelineRole Role);

public record RemoveRoleBindingCommand(RemoveRoleBindingRequest Request) : ICommand<Result>;

public record GetModelRegistryCommand(Guid UserId) : ICommand<Result<ModelRegistryDto>>;

public record SaveAgentTemplateRequest(
    Guid? Id,
    Guid UserId,
    string Name,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);

public record SaveAgentTemplateCommand(SaveAgentTemplateRequest Request) : ICommand<Result<AgentTemplateDto>>;

public record DeleteAgentTemplateRequest(Guid Id, Guid UserId);

public record DeleteAgentTemplateCommand(DeleteAgentTemplateRequest Request) : ICommand<Result>;

public record GetProxyStatusCommand : ICommand<Result<LlamaSwapStatusDto>>;

public record ReloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;

public record UnloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;
