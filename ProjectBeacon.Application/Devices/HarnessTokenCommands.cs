namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Runtime;
using Application.Security;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Common;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Issues a one-shot API credential for a currently running workstation chat command.
/// The raw credential is returned once only; the database stores its hash.
/// </summary>
public sealed record IssueHarnessTokenCommand(Guid CommandId, int Generation, ActorContext Actor)
    : ICommand<Result<HarnessTokenDto>>;

/// <summary>Ephemeral credential returned only to the authenticated workstation.</summary>
public sealed record HarnessTokenDto(string Token, Guid ProjectId, Guid CommandId, int Generation, DateTime ExpiresAt, IReadOnlyList<string>? AllowedMcpTools = null, IReadOnlyList<McpServerConfiguration>? McpServers = null);

public sealed class IssueHarnessTokenHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets)
    : ICommandHandler<IssueHarnessTokenCommand, Result<HarnessTokenDto>>
{
    private const ApiTokenCapability HarnessCapabilities =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;

    public async Task<Result<HarnessTokenDto>> HandleAsync(
        IssueHarnessTokenCommand command,
        CancellationToken ct = default)
    {
        var actor = command.Actor;
        if (!actor.IsDevice || actor.DeviceId is not { } deviceId || deviceId == Guid.Empty)
            return Result.Failure<HarnessTokenDto>("A device token is required.", ErrorKind.Forbidden);
        if (command.Generation < 1)
            return Result.Failure<HarnessTokenDto>("A valid command generation is required.", ErrorKind.Conflict);

        await using var db = dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || device.IsRevoked || !device.IsOnline(now))
            return Result.Failure<HarnessTokenDto>("The workstation is offline or revoked.", ErrorKind.Forbidden);

        var work = await db.WorkstationCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == command.CommandId && c.DeviceId == deviceId, ct);
        if (work is null
            || work.Kind is not (WorkstationCommandKind.ChatPrompt or WorkstationCommandKind.RunPipelineSession or WorkstationCommandKind.RunEvalTurn)
            || work.ProjectId is not { } projectId
            || work.Status != WorkstationCommandStatus.Running
            || work.StartedAt is null
            || work.LeaseExpiresAt is not { } leaseExpiresAt
            || leaseExpiresAt <= now)
        {
            return Result.Failure<HarnessTokenDto>("The harness command is not running under an active lease.", ErrorKind.Forbidden);
        }

        if (work.Attempts + 1 != command.Generation)
            return Result.Failure<HarnessTokenDto>("The chat command generation is stale.", ErrorKind.Conflict);

        if (!string.Equals(work.ClaimInstanceId, device.ActiveInstanceId, StringComparison.Ordinal))
            return Result.Failure<HarnessTokenDto>("The command belongs to a different workstation process instance.", ErrorKind.Forbidden);

        IReadOnlyList<string>? allowedMcpTools = null;
        if (work.Kind == WorkstationCommandKind.RunPipelineSession)
        {
            if (!WorkstationCommandPayloads.TryReadPipelineSession(work.PayloadJson, out var scope) || scope.ProjectId != projectId)
                return Result.Failure<HarnessTokenDto>("The pipeline command has an invalid session scope.", ErrorKind.Forbidden);

            var session = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == scope.SessionId && s.ProjectId == projectId, ct);
            var taskExists = await db.Tasks.IgnoreQueryFilters()
                .AnyAsync(t => t.Id == scope.TaskId && t.ProjectId == projectId, ct);
            if (session is null
                || !taskExists
                || session.Status != SessionStatus.Active
                || session.TaskId != scope.TaskId
                || session.Role != scope.Role
                || session.SubtaskId != scope.SubtaskId)
                return Result.Failure<HarnessTokenDto>("The pipeline command does not match its active database session.", ErrorKind.Forbidden);

            if (scope.Role == PipelineRole.Actor)
            {
                if (scope.SubtaskId is not { } subtaskId)
                    return Result.Failure<HarnessTokenDto>("Actor sessions require a subtask scope.", ErrorKind.Forbidden);

                var subtask = await db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == subtaskId
                        && s.TaskId == scope.TaskId
                        && s.ProjectId == projectId, ct);
                if (subtask is null || subtask.Status != SubtaskStatus.InProgress)
                    return Result.Failure<HarnessTokenDto>("The Actor subtask is not active for this pipeline session.", ErrorKind.Forbidden);

                allowedMcpTools = subtask.AllowedMcpTools.ToArray();
            }
            else
            {
                if (scope.SubtaskId is not null)
                    return Result.Failure<HarnessTokenDto>("Only Actor sessions may carry a subtask scope.", ErrorKind.Forbidden);
                allowedMcpTools = [];
            }
        }

        else if (work.Kind == WorkstationCommandKind.RunEvalTurn)
        {
            if (!WorkstationCommandPayloads.TryReadEvalTurn(work.PayloadJson, out var evalScope))
                return Result.Failure<HarnessTokenDto>("The eval command has an invalid run scope.", ErrorKind.Forbidden);

            var evalRun = await db.EvalRuns.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(run => run.Id == evalScope.EvalRunId
                    && run.ProjectId == projectId
                    && run.TaskId == evalScope.TaskId, ct);
            if (evalRun is null || !string.Equals(evalRun.Runtime, evalScope.Runtime, StringComparison.Ordinal))
                return Result.Failure<HarnessTokenDto>("The eval run is missing, out of scope, or its pinned runtime does not match the command.", ErrorKind.Forbidden);
        }

        // External MCP credentials are decrypted only for this short-lived, device-authenticated
        // response. They are never returned by the human project-settings API or written to logs.
        IReadOnlyList<McpServerConfiguration> mcpServers = [];
        var project = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == projectId, ct);
        if (project is null)
            return Result.Failure<HarnessTokenDto>("The harness project no longer exists.", ErrorKind.Forbidden);
        var mayUseExternalMcp = work.Kind == WorkstationCommandKind.ChatPrompt
            || (work.Kind == WorkstationCommandKind.RunPipelineSession
                && allowedMcpTools?.Any(tool => tool.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)) == true);
        if (mayUseExternalMcp && !string.IsNullOrWhiteSpace(project.McpServersCipher))
        {
            try
            {
                var json = secrets.Open(project.McpServersCipher);
                var configured = JsonSerializer.Deserialize<List<McpServerConfiguration>>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                mcpServers = configured
                    .Where(server => server.Enabled)
                    .Where(server => work.Kind == WorkstationCommandKind.ChatPrompt
                        || allowedMcpTools?.Any(allowed => allowed.StartsWith(
                            $"mcp_{server.Name.ToLowerInvariant()}_", StringComparison.OrdinalIgnoreCase)) == true)
                    .ToArray();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
            {
                return Result.Failure<HarnessTokenDto>("The project's MCP configuration cannot be decrypted. Reconfigure its MCP servers before starting a harness session.");
            }
        }


        // A renewed claim must never inherit a credential from an earlier attempt.
        var previous = await db.ApiTokens.IgnoreQueryFilters()
            .Where(t => t.ClientKind == ApiTokenClientKind.Harness
                && t.CommandId == work.Id
                && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in previous)
            token.Revoke();

        var rawToken = "bcn_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        var tokenPrefix = rawToken[..Math.Min(8, rawToken.Length)];
        var tokenCapabilities = work.Kind == WorkstationCommandKind.RunEvalTurn
            ? ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead
            : HarnessCapabilities;
        var tokenRow = ApiToken.Create(
            $"harness:{work.Id:N}",
            tokenHash,
            tokenPrefix,
            projectId,
            tokenCapabilities,
            leaseExpiresAt,
            device.UserId,
            ApiTokenClientKind.Harness,
            deviceId,
            work.Id);

        db.ApiTokens.Add(tokenRow);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new HarnessTokenDto(rawToken, projectId, work.Id, command.Generation, leaseExpiresAt, allowedMcpTools, mcpServers));
    }

}
