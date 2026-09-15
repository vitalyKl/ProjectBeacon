namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Domain.Enums;

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

public record LlamaSwapStatusDto(
    bool Available,
    bool Healthy,
    string? LoadedModel,
    string? Memory,
    DateTime? LastSwap,
    string? Error);

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

public interface ILlamaSwapProxy
{
    Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<bool> ReloadAsync(CancellationToken ct = default);
    Task<bool> UnloadAsync(CancellationToken ct = default);
}

public sealed class UnavailableLlamaSwapProxy : ILlamaSwapProxy
{
    public Task<LlamaSwapStatusDto> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new LlamaSwapStatusDto(false, false, null, null, null, "llama-swap supervisor is not running."));

    public Task<bool> ReloadAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> UnloadAsync(CancellationToken ct = default) => Task.FromResult(false);
}
