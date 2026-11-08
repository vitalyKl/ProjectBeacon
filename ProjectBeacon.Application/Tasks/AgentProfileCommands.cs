namespace ProjectBeacon.Application.Tasks;

using Application.Common;

/// <summary>A user-owned customization layered over one immutable built-in execution profile.</summary>
public sealed record AgentProfileOverrideDto(
    Guid UserId,
    string Key,
    string Name,
    string Description,
    string Instruction,
    string Mode,
    IReadOnlyList<string> Tools,
    string PathPolicy,
    IReadOnlyList<string> DefaultPaths,
    bool CommandsEnabled,
    bool ReportsDiff,
    string SizeHint,
    string Effort);

public sealed record GetAgentProfileOverridesCommand(Guid UserId)
    : ICommand<Result<IReadOnlyList<AgentProfileOverrideDto>>>;

public sealed record SaveAgentProfileOverrideRequest(
    Guid UserId,
    string Key,
    string Name,
    string Description,
    string Instruction,
    IReadOnlyList<string> Tools,
    bool CommandsEnabled,
    bool ReportsDiff,
    string SizeHint,
    string Effort);

public sealed record SaveAgentProfileOverrideCommand(SaveAgentProfileOverrideRequest Request)
    : ICommand<Result<AgentProfileOverrideDto>>;

public sealed record ResetAgentProfileOverrideRequest(Guid UserId, string Key);

public sealed record ResetAgentProfileOverrideCommand(ResetAgentProfileOverrideRequest Request)
    : ICommand<Result>;
