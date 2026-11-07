namespace ProjectBeacon.Domain.Common;

/// <summary>Canonical HTTP route templates and client path builders for daemon APIs.</summary>
public static class ApiRoutes
{
    public const string DeviceHeartbeatRoute = "/v1/devices/me/heartbeat";
    public const string DeviceCommandsRoute = "/v1/devices/me/commands";
    public const string CommandCompleteRoute = "/v1/commands/{id:guid}/complete";
    public const string CommandProgressRoute = "/v1/commands/{id:guid}/progress";
    public const string CommandHarnessTokenRoute = "/v1/commands/{id:guid}/harness-token";
    public const string DeviceProviderConnections = "/v1/devices/me/provider-connections";
    public const string ChatSessionPartsRoute = "/v1/chat/sessions/{id:guid}/parts";
    public const string ChatSessionIdleRoute = "/v1/chat/sessions/{id:guid}/idle";
    public const string ChatSessionUsageRoute = "/v1/chat/sessions/{id:guid}/usage";

    public const string DeviceHeartbeat = DeviceHeartbeatRoute;
    public const string DeviceCommandsClaim = DeviceCommandsRoute + "?wait=25";
    public const string DeviceCommandsControlClaim = DeviceCommandsRoute + "?wait=1&controlOnly=true";

    public static string CommandComplete(Guid commandId) => $"/v1/commands/{commandId:D}/complete";
    public static string CommandProgress(Guid commandId) => $"/v1/commands/{commandId:D}/progress";
    public static string CommandHarnessToken(Guid commandId) => $"/v1/commands/{commandId:D}/harness-token";
    public static string ChatSessionParts(Guid sessionId) => $"/v1/chat/sessions/{sessionId:D}/parts";
    public static string ChatSessionIdle(Guid sessionId) => $"/v1/chat/sessions/{sessionId:D}/idle";
    public static string ChatSessionUsage(Guid sessionId) => $"/v1/chat/sessions/{sessionId:D}/usage";
}

/// <summary>Empty JSON body used by compatibility device calls that require a POST.</summary>
public sealed record EmptyRequest();

/// <summary>Request body used to report workstation heartbeat state.</summary>
public sealed record WorkstationHeartbeatRequest(string? ProbeJson, string? WorkstationJson, string? InstanceId = null);

/// <summary>Request body used to complete a claimed workstation command.</summary>
public sealed record CommandCompletionRequest(bool Success, string? ResultJson, string? Error, int? Generation = null);

/// <summary>Request body used to report progress for a claimed command generation.</summary>
public sealed record CommandProgressRequest(int Generation, string ProgressJson);

/// <summary>Request body used to issue a short-lived harness credential for a claim generation.</summary>
public sealed record HarnessTokenRequest(int Generation);

/// <summary>Request body used to append one structured chat part.</summary>
public sealed record ChatPartRequest(
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    string? Tool = null,
    string? ToolStatus = null,
    string? ToolInput = null,
    string? ToolResult = null,
    int? DurationMs = null,
    bool Complete = false);

/// <summary>Request body used to report chat token usage and context capacity.</summary>
public sealed record ChatUsageReportRequest(
    int PromptTokens,
    int CompletionTokens,
    int AssistantMessages,
    double TotalCost,
    int? ContextLimit = null,
    double GenerationSeconds = 0);
