namespace ProjectBeacon.Application.Runtime;

/// <summary>
/// Project-scoped MCP server configuration. Environment values and HTTP headers are secrets:
/// this type is sent only in the short-lived device harness-token response, never in project DTOs.
/// </summary>
public sealed record McpServerConfiguration(
    string Name,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? Endpoint = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Enabled = true);

/// <summary>Safe project-settings view of an MCP server. Secret values are never returned.</summary>
public sealed record ProjectMcpServerDto(
    string Name,
    string Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? Endpoint,
    IReadOnlyList<string> EnvironmentKeys,
    IReadOnlyList<string> HeaderNames,
    bool HasSecrets,
    bool Enabled);

/// <summary>One server in an atomic project MCP configuration update.</summary>
public sealed record ProjectMcpServerSaveItem(McpServerConfiguration Server, bool ReplaceSecrets = false);
