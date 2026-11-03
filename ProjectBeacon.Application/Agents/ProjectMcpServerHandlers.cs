namespace ProjectBeacon.Application.Agents;

using System.Security.Cryptography;
using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Runtime;
using Application.Security;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reads the configured MCP servers for a project. Secret values are deliberately omitted.
/// </summary>
public sealed record ListProjectMcpServersCommand(Guid ProjectId, ActorContext Actor)
    : ICommand<Result<IReadOnlyList<ProjectMcpServerDto>>>;

/// <summary>
/// Replaces the complete project MCP-server list in one transaction. Existing credentials are
/// retained for matching server names unless ReplaceSecrets is explicitly set.
/// </summary>
public sealed record SaveProjectMcpServersCommand(
    Guid ProjectId,
    IReadOnlyList<ProjectMcpServerSaveItem> Servers,
    ActorContext Actor)
    : ICommand<Result<IReadOnlyList<ProjectMcpServerDto>>>;

public sealed class ListProjectMcpServersHandler(
    IBeaconDbFactory dbFactory,
    IAuthorizationService authorization,
    ISecretProtector secrets)
    : ICommandHandler<ListProjectMcpServersCommand, Result<IReadOnlyList<ProjectMcpServerDto>>>
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<IReadOnlyList<ProjectMcpServerDto>>> HandleAsync(
        ListProjectMcpServersCommand command,
        CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var access = await authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: command.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<IReadOnlyList<ProjectMcpServerDto>>();

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("Project not found.");

        var loaded = ReadStored(project.McpServersCipher, secrets);
        if (!loaded.Success)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>(loaded.Error!);
        return Result.Ok<IReadOnlyList<ProjectMcpServerDto>>(loaded.Value!.Select(Map).ToArray());
    }

    internal static Result<IReadOnlyList<McpServerConfiguration>> ReadStored(
        string? cipher,
        ISecretProtector secrets)
    {
        if (string.IsNullOrWhiteSpace(cipher))
            return Result.Ok<IReadOnlyList<McpServerConfiguration>>([]);

        try
        {
            var json = secrets.Open(cipher);
            var servers = JsonSerializer.Deserialize<List<McpServerConfiguration>>(json, JsonOptions) ?? [];
            return Result.Ok<IReadOnlyList<McpServerConfiguration>>(servers);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return Result.Failure<IReadOnlyList<McpServerConfiguration>>(
                "The project's stored MCP configuration cannot be decrypted or parsed. Reconfigure the MCP servers to recover.");
        }
    }

    internal static ProjectMcpServerDto Map(McpServerConfiguration server)
    {
        var environmentKeys = server.Environment?.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var headerNames = server.Headers?.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return new ProjectMcpServerDto(
            server.Name,
            server.Transport,
            server.Command,
            server.Arguments ?? [],
            server.Endpoint,
            environmentKeys,
            headerNames,
            environmentKeys.Length > 0 || headerNames.Length > 0,
            server.Enabled);
    }
}

public sealed class SaveProjectMcpServersHandler(
    IBeaconDbFactory dbFactory,
    IAuthorizationService authorization,
    ISecretProtector secrets)
    : ICommandHandler<SaveProjectMcpServersCommand, Result<IReadOnlyList<ProjectMcpServerDto>>>
{
    private const int MaxServers = 32;
    private const int MaxArguments = 64;
    private const int MaxSecretEntries = 64;

    public async Task<Result<IReadOnlyList<ProjectMcpServerDto>>> HandleAsync(
        SaveProjectMcpServersCommand command,
        CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("Project id is required.");
        if (command.Servers is null || command.Servers.Count > MaxServers)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>($"A project can have at most {MaxServers} MCP servers.");

        await using var db = dbFactory.CreateDbContext();
        var access = await authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: command.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden<IReadOnlyList<ProjectMcpServerDto>>();

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("Project not found.");

        var oldResult = ListProjectMcpServersHandler.ReadStored(project.McpServersCipher, secrets);
        if (!oldResult.Success)
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>(oldResult.Error!);

        var existing = oldResult.Value!.ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase);
        var updated = new List<McpServerConfiguration>(command.Servers.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in command.Servers)
        {
            if (item?.Server is null)
                return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("An MCP server entry is empty.");

            if (!TryNormalize(item.Server, out var normalized, out var error))
                return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>(error!);

            if (!seen.Add(normalized!.Name))
                return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>($"MCP server name '{normalized.Name}' is duplicated.");

            if (!item.ReplaceSecrets
                && existing.TryGetValue(normalized.Name, out var previous)
                && string.Equals(previous.Transport, normalized.Transport, StringComparison.Ordinal))
            {
                normalized = normalized with
                {
                    Environment = normalized.Environment is { Count: > 0 }
                        ? normalized.Environment
                        : previous.Environment,
                    Headers = normalized.Headers is { Count: > 0 }
                        ? normalized.Headers
                        : previous.Headers
                };
            }

            updated.Add(normalized);
        }

        try
        {
            var json = JsonSerializer.Serialize(updated, ListProjectMcpServersHandler.JsonOptions);
            if (json.Length > 256_000)
                return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("The project MCP configuration exceeds the 256 KB limit.");
            project.SetMcpServersCipher(secrets.Seal(json));
            await db.SaveChangesAsync(ct);
        }
        catch (CryptographicException)
        {
            return Result.Failure<IReadOnlyList<ProjectMcpServerDto>>("The MCP configuration could not be encrypted.");
        }

        return Result.Ok<IReadOnlyList<ProjectMcpServerDto>>(updated.Select(ListProjectMcpServersHandler.Map).ToArray());
    }

    private static bool TryNormalize(
        McpServerConfiguration input,
        out McpServerConfiguration? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 24
            || !IsNameStart(name[0])
            || name.Any(character => !IsNameCharacter(character)))
        {
            error = "Server name must start with a letter or digit and contain only letters, digits, '_' or '-' (max 24 characters).";
            return false;
        }
        if (name.Equals("beacon", StringComparison.OrdinalIgnoreCase))
        {
            error = "'beacon' is reserved for ProjectBeacon's built-in tools.";
            return false;
        }

        var transport = input.Transport?.Trim().ToLowerInvariant() ?? string.Empty;
        var environment = NormalizeEnvironment(input.Environment, out error);
        if (error is not null)
            return false;
        var headers = NormalizeHeaders(input.Headers, out error);
        if (error is not null)
            return false;

        if (transport == "stdio")
        {
            var command = input.Command?.Trim() ?? string.Empty;
            var args = input.Arguments ?? [];
            if (command.Length is 0 or > 500 || command.Any(char.IsControl))
            {
                error = $"Local MCP server '{name}' requires an executable command (max 500 characters).";
                return false;
            }
            if (args.Count > MaxArguments || args.Any(arg => arg is null || arg.Length > 2000 || arg.Any(char.IsControl)))
            {
                error = $"Local MCP server '{name}' has invalid arguments (max {MaxArguments} arguments, 2,000 characters each).";
                return false;
            }
            if (headers is { Count: > 0 } || !string.IsNullOrWhiteSpace(input.Endpoint))
            {
                error = "A stdio MCP server uses a command, arguments and environment variables; HTTP fields are not allowed.";
                return false;
            }
            normalized = new McpServerConfiguration(name, transport, command, args.ToArray(), null, environment, null, input.Enabled);
            return true;
        }

        if (transport == "streamable-http")
        {
            var rawEndpoint = input.Endpoint?.Trim();
            if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("https" or "http")
                || (endpoint.Scheme == "http" && !endpoint.IsLoopback))
            {
                error = "Streamable HTTP MCP requires an absolute HTTPS endpoint. Plain HTTP is allowed only for loopback addresses.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(input.Command)
                || (input.Arguments?.Count ?? 0) > 0
                || environment is { Count: > 0 })
            {
                error = "A streamable HTTP MCP server uses an endpoint and HTTP headers; local process fields are not allowed.";
                return false;
            }
            normalized = new McpServerConfiguration(name, transport, null, [], endpoint.AbsoluteUri, null, headers, input.Enabled);
            return true;
        }

        error = "Transport must be 'stdio' or 'streamable-http'.";
        return false;
    }

    private static Dictionary<string, string>? NormalizeEnvironment(
        IReadOnlyDictionary<string, string>? values,
        out string? error)
    {
        error = null;
        if (values is null || values.Count == 0)
            return null;
        if (values.Count > MaxSecretEntries)
        {
            error = $"A maximum of {MaxSecretEntries} environment variables is allowed per MCP server.";
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            var value = pair.Value ?? string.Empty;
            if (key.Length is < 1 or > 100
                || !IsEnvironmentName(key)
                || key.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                || key.Equals("HOME", StringComparison.OrdinalIgnoreCase)
                || key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SYSTEMROOT", StringComparison.OrdinalIgnoreCase)
                || key.Equals("WINDIR", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Environment variable name '{key}' is invalid or reserved by ProjectBeacon.";
                return null;
            }
            if (value.Length > 4000 || value.Contains('\0'))
            {
                error = $"Environment variable '{key}' exceeds the 4,000 character limit or contains a null character.";
                return null;
            }
            result[key] = value;
        }
        return result;
    }

    private static Dictionary<string, string>? NormalizeHeaders(
        IReadOnlyDictionary<string, string>? values,
        out string? error)
    {
        error = null;
        if (values is null || values.Count == 0)
            return null;
        if (values.Count > MaxSecretEntries)
        {
            error = $"A maximum of {MaxSecretEntries} HTTP headers is allowed per MCP server.";
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            var value = pair.Value ?? string.Empty;
            if (key.Length is < 1 or > 100 || key.Any(character => !IsHeaderNameCharacter(character)))
            {
                error = $"HTTP header name '{key}' is invalid.";
                return null;
            }
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MCP-Session-Id", StringComparison.OrdinalIgnoreCase)
                || key.Equals("MCP-Protocol-Version", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                error = $"HTTP header '{key}' is reserved by the MCP transport.";
                return null;
            }
            if (value.Length > 4000 || value.Contains('\r') || value.Contains('\n'))
            {
                error = $"HTTP header '{key}' exceeds the 4,000 character limit or contains a line break.";
                return null;
            }
            result[key] = value;
        }
        return result;
    }

    private static bool IsNameStart(char value) => char.IsAsciiLetterOrDigit(value);
    private static bool IsNameCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '-';
    private static bool IsEnvironmentName(string value) =>
        (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool IsHeaderNameCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
