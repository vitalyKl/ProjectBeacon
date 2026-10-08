namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Domain.Enums;
using Infrastructure.LlamaSwap;
/// <summary>
/// One user-owned model backend, including whether it may stay resident with other models.
/// </summary>
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
    string Note = "",
    string OpenCodeModel = "");
/// <summary>
/// A project pipeline role bound to a model backend.
/// </summary>
public record RoleBindingDto(
    Guid Id,
    PipelineRole Role,
    Guid ModelBackendId,
    Guid ProjectId);
/// <summary>
/// A saved solo, planner, actor, and review backend selection.
/// </summary>
public record AgentTemplateDto(
    Guid Id,
    string Name,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);
/// <summary>
/// The user's backends and templates plus the current project's role bindings.
/// </summary>
public record ModelRegistryDto(
    Guid UserId,
    IReadOnlyList<LocalModelBackendDto> Backends,
    IReadOnlyList<RoleBindingDto> Bindings,
    IReadOnlyList<AgentTemplateDto> Templates);
/// <summary>
/// Create or update a backend. Id is null on create. Concurrent marks the resident group.
/// </summary>
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
    string? Note = null,
    string? OpenCodeModel = null);
/// <summary>
/// Command for upsert local model backend.
/// </summary>
public record UpsertLocalModelBackendCommand(UpsertLocalModelBackendRequest Request) : ICommand<Result<LocalModelBackendDto>>;
/// <summary>
/// Fields for delete local model backend.
/// </summary>
public record DeleteLocalModelBackendRequest(Guid Id, Guid UserId);
/// <summary>
/// Command for delete local model backend.
/// </summary>
public record DeleteLocalModelBackendCommand(DeleteLocalModelBackendRequest Request) : ICommand<Result>;
/// <summary>
/// Fields for set role binding.
/// </summary>
public record SetRoleBindingRequest(PipelineRole Role, Guid ModelBackendId);
/// <summary>
/// Command for set role binding.
/// </summary>
public record SetRoleBindingCommand(SetRoleBindingRequest Request) : ICommand<Result<RoleBindingDto>>;
/// <summary>
/// Fields for remove role binding.
/// </summary>
public record RemoveRoleBindingRequest(PipelineRole Role);
/// <summary>
/// Command for remove role binding.
/// </summary>
public record RemoveRoleBindingCommand(RemoveRoleBindingRequest Request) : ICommand<Result>;
/// <summary>
/// Command for get model registry.
/// </summary>
public record GetModelRegistryCommand(Guid UserId) : ICommand<Result<ModelRegistryDto>>;
/// <summary>
/// Fields for save agent template.
/// </summary>
public record SaveAgentTemplateRequest(
    Guid? Id,
    Guid UserId,
    string Name,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);
/// <summary>
/// Command for save agent template.
/// </summary>
public record SaveAgentTemplateCommand(SaveAgentTemplateRequest Request) : ICommand<Result<AgentTemplateDto>>;
/// <summary>
/// Fields for delete agent template.
/// </summary>
public record DeleteAgentTemplateRequest(Guid Id, Guid UserId);
/// <summary>
/// Command for delete agent template.
/// </summary>
public record DeleteAgentTemplateCommand(DeleteAgentTemplateRequest Request) : ICommand<Result>;
/// <summary>
/// Asks for the workstation llama-swap status. No arguments.
/// </summary>
public record GetProxyStatusCommand : ICommand<Result<LlamaSwapStatusDto>>;
/// <summary>
/// Asks the workstation to reload llama-swap. No arguments.
/// </summary>
public record ReloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;
/// <summary>
/// Asks the workstation to unload llama-swap. No arguments.
/// </summary>
public record UnloadProxyCommand : ICommand<Result<LlamaSwapStatusDto>>;
