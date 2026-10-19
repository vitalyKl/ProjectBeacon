namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Auth;
using Application.Common;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
/// <summary>
/// Queues a command for an online device. Project commands need a runtime, and the payload is sandboxed.
/// </summary>
public class EnqueueCommandHandler : ICommandHandler<EnqueueCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public EnqueueCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(EnqueueCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Device not found.");
        if (!device.IsOnline(DateTime.UtcNow))
            return Result.Failure<WorkstationCommandDto>("Device is not connected.", ErrorKind.Conflict);

        if (command.Request.ProjectId is { } projectId)
        {
            var member = await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == command.Request.UserId, ct);
            if (!member)
                return Result.Failure<WorkstationCommandDto>("Project not found.");
        }

        string? payloadJson = command.Request.PayloadJson;
        string? localRoot = null;
        if (command.Request.Kind == WorkstationCommandKind.SaveWorkstation)
        {
            if (!DesiredState.TryStampWorkstation(payloadJson, out var stored, out var error))
                return Result.Failure<WorkstationCommandDto>(error ?? "Invalid payload.");
            // Optimistic concurrency: the payload must declare the revision its content was
            // based on. The JSON and the incremented revision commit as one UPDATE guarded by
            // that base revision, so a stale client's content can never be stamped as the
            // latest revision — it gets a conflict and must reload and re-apply its edits.
            var baseRevision = DesiredState.ReadRevision(payloadJson);
            if (baseRevision is null)
                return Result.Failure<WorkstationCommandDto>(
                    "payload must include the revision the content is based on.", ErrorKind.Conflict);
            // One short transaction over this single context: the guarded UPDATE and the
            // command INSERT commit together, so a failed insert rolls back the JSON and
            // the revision with it, and a retry with the same base revision succeeds.
            // The whole transaction sits inside the execution strategy: the production
            // Npgsql options enable retrying, which does not support user-initiated
            // transactions started outside a strategy (the ClaimTaskHandler pattern).
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var written = await db.DaemonDevices
                    .Where(d => d.Id == device.Id && d.DesiredRevision == baseRevision.Value)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.DesiredWorkstationJson, stored)
                        .SetProperty(d => d.DesiredRevision, baseRevision.Value + 1), ct);
                if (written == 0)
                {
                    // PostgreSQL READ COMMITTED snapshots per statement, so this reread sees
                    // the rival's committed revision for the error message.
                    var current = await db.DaemonDevices.AsNoTracking()
                        .Where(d => d.Id == device.Id)
                        .Select(d => (long?)d.DesiredRevision)
                        .FirstOrDefaultAsync(ct);
                    await tx.RollbackAsync(ct);
                    return Result.Failure<WorkstationCommandDto>(current is null
                        ? "Device not found."
                        : $"Workstation config moved from revision {baseRevision.Value} to {current} while saving; reload and re-apply your changes.",
                        ErrorKind.Conflict);
                }
                var queuedSave = WorkstationCommand.Create(
                    device.Id,
                    command.Request.Kind,
                    DesiredState.WithRevision(stored, baseRevision.Value + 1),
                    command.Request.ProjectId,
                    command.Request.UserId);
                db.WorkstationCommands.Add(queuedSave);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result.Ok(MapCommand(queuedSave));
            });
        }
        if (CommandSandbox.IsProjectKind(command.Request.Kind))
        {
            if (command.Request.ProjectId is not { } rootedProjectId)
                return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired);
            var resolved = await ProjectRuntimeResolver.ResolveAsync(db, rootedProjectId, device.Id, ct);
            if (!resolved.Success)
                return Result.Failure<WorkstationCommandDto>(resolved.Error ?? CommandSandbox.RuntimeRequired);
            var runtime = resolved.Value!;
            var sanitized = CommandSandbox.SanitizeProjectPayload(payloadJson, command.Request.Kind);
            if (!sanitized.Success)
                return Result.Failure<WorkstationCommandDto>(sanitized.Error ?? "Invalid payload.");
            payloadJson = sanitized.Value;
            localRoot = runtime.LocalRoot;
            if (command.Request.Kind == WorkstationCommandKind.RunReviewCheck)
            {
                if (string.IsNullOrWhiteSpace(command.Request.TrustedCheckCommand))
                    return Result.Failure<WorkstationCommandDto>("A trusted review check command is required.", ErrorKind.Conflict);
                var json = JsonNode.Parse(payloadJson ?? "{}") as JsonObject ?? new JsonObject();
                json["checkCommand"] = command.Request.TrustedCheckCommand.Trim();
                payloadJson = json.ToJsonString();
            }
            if (command.Request.Kind == WorkstationCommandKind.ApplyOpencode)
                payloadJson = DesiredState.WithRevision(payloadJson ?? "{}", runtime.ConfigRevision);
        }

        var queued = WorkstationCommand.Create(
            device.Id,
            command.Request.Kind,
            payloadJson,
            command.Request.ProjectId,
            command.Request.UserId);
        db.WorkstationCommands.Add(queued);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapCommand(queued, localRoot));
    }

    internal static WorkstationCommandDto MapCommand(WorkstationCommand command, string? localRoot = null) =>
        new(command.Id, command.DeviceId, command.ProjectId, command.RequestedByUserId, command.Kind, command.Status,
            command.PayloadJson, command.ResultJson, command.Error, command.CreatedAt, command.StartedAt, command.CompletedAt, localRoot,
            command.Version, command.Attempts + 1);
}
/// <summary>
/// Returns the next pending command for the device, or null when the queue is empty.
/// Claiming is a guarded <c>UPDATE ... WHERE Id = @id AND Status = 'Pending'</c>; a concurrent
/// worker that claims the same row first makes the update affect zero rows, so the loser moves
/// on to the next command instead of double-claiming.
/// </summary>
public class ClaimNextCommandHandler : ICommandHandler<ClaimNextCommandCommand, Result<WorkstationCommandDto?>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ClaimNextCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto?>> HandleAsync(ClaimNextCommandCommand command, CancellationToken ct = default)
    {
        var wait = command.Request.Wait ?? TimeSpan.Zero;
        var deadline = DateTime.UtcNow + wait;
        string? instanceId = null;
        {
            await using var db = _dbFactory.CreateDbContext();
            instanceId = await db.DaemonDevices.AsNoTracking()
                .Where(d => d.Id == command.Request.DeviceId)
                .Select(d => d.ActiveInstanceId)
                .FirstOrDefaultAsync(ct);
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = _dbFactory.CreateDbContext();
            var next = await db.WorkstationCommands.AsNoTracking()
                .Where(c => c.DeviceId == command.Request.DeviceId && c.Status == WorkstationCommandStatus.Pending)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (next is null)
            {
                if (DateTime.UtcNow >= deadline)
                    return Result.Ok<WorkstationCommandDto?>(null);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                catch (OperationCanceledException)
                {
                    return Result.Ok<WorkstationCommandDto?>(null);
                }
                continue;
            }

            string? localRoot = null;
            if (CommandSandbox.IsProjectKind(next.Kind))
            {
                localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, next, ct);
                if (string.IsNullOrWhiteSpace(localRoot))
                {
                    // Guarded fail: only take effect if the row is still pending.
                    var now = DateTime.UtcNow;
                    var updated = await db.WorkstationCommands
                        .Where(c => c.Id == next.Id && c.Status == WorkstationCommandStatus.Pending)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(c => c.Status, WorkstationCommandStatus.Failed)
                            .SetProperty(c => c.Error, CommandSandbox.RuntimeRequired)
                            .SetProperty(c => c.CompletedAt, now)
                            .SetProperty(c => c.StartedAt, now), ct);
                    if (updated == 1)
                        continue;
                    // 0 rows: another worker already advanced this row; retry the queue.
                    continue;
                }
            }

            // Guarded claim: succeeds only if the row is still pending. The claim grants the
            // kind's execution lease and records the claiming process instance.
            var startedAt = DateTime.UtcNow;
            var lease = CommandLease.For(next.Kind);
            var claimed = await db.WorkstationCommands
                .Where(c => c.Id == next.Id && c.Status == WorkstationCommandStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, WorkstationCommandStatus.Running)
                    .SetProperty(c => c.StartedAt, startedAt)
                    .SetProperty(c => c.LeaseExpiresAt, startedAt + lease)
                    .SetProperty(c => c.ClaimInstanceId, instanceId), ct);
            if (claimed != 1)
                continue; // lost the race; the next pending command (if any) is picked up next iteration.

            return Result.Ok<WorkstationCommandDto?>(new WorkstationCommandDto(
                next.Id, next.DeviceId, next.ProjectId, next.RequestedByUserId, next.Kind,
                WorkstationCommandStatus.Running, next.PayloadJson, next.ResultJson, null,
                next.CreatedAt, startedAt, null, localRoot, next.Version, next.Attempts + 1));
        }
    }
}
/// <summary>
/// Marks a device command succeeded or failed. The finish is a guarded
/// <c>UPDATE ... WHERE Id = @id AND Status = @expected</c>; a concurrent claim, re-queue,
/// or completion that moved the row first makes the update affect zero rows and the loser
/// reports failure without applying the completion effects.
/// </summary>
public class CompleteCommandHandler : ICommandHandler<CompleteCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CompleteCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(CompleteCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null || row.DeviceId != command.Request.DeviceId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        // A late completion from an older attempt must not finish the attempt the row is on now.
        if (!CommandCompletions.AcceptsGeneration(row, command.Request.Generation))
            return Result.Failure<WorkstationCommandDto>("Command generation mismatch.");

        var expectedStatus = row.Status;
        var error = command.Request.Success
            ? null
            : string.IsNullOrWhiteSpace(command.Request.Error) ? "Command failed." : command.Request.Error;
        try
        {
            if (command.Request.Success)
                row.Succeed(command.Request.ResultJson);
            else
                row.Fail(error!);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<WorkstationCommandDto>(ex.Message);
        }

        var finished = await db.WorkstationCommands
            .Where(c => c.Id == row.Id && c.Status == expectedStatus)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, row.Status)
                .SetProperty(c => c.ResultJson, row.ResultJson)
                .SetProperty(c => c.Error, row.Error)
                .SetProperty(c => c.StartedAt, row.StartedAt)
                .SetProperty(c => c.CompletedAt, row.CompletedAt)
                .SetProperty(c => c.LeaseExpiresAt, row.LeaseExpiresAt), ct);
        if (finished == 0)
            return Result.Failure<WorkstationCommandDto>("Command state changed while completing; retry.");

        // Completion effects run only for the winning transition.
        try
        {
            await CommandCompletions.ApplyAsync(db, row, command.Request.Success, command.Request.ResultJson, error, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<WorkstationCommandDto>(ex.Message);
        }
        await db.SaveChangesAsync(ct);
        var localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, row, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
    }

}
/// <summary>
/// Loads one command the user owns.
/// </summary>
public class GetCommandHandler : ICommandHandler<GetCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(GetCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, row, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
    }
}
/// <summary>
/// Lists recent commands for a device the user owns.
/// </summary>
public class ListCommandsHandler : ICommandHandler<ListCommandsCommand, Result<IList<WorkstationCommandDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListCommandsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<WorkstationCommandDto>>> HandleAsync(ListCommandsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<IList<WorkstationCommandDto>>("Device not found.");
        var limit = Math.Clamp(command.Request.Limit, 1, 100);
        var rows = await db.WorkstationCommands
            .Where(c => c.DeviceId == command.Request.DeviceId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        var projectIds = rows
            .Where(r => r.ProjectId is not null && CommandSandbox.IsProjectKind(r.Kind))
            .Select(r => r.ProjectId!.Value)
            .Distinct()
            .ToList();
        var roots = await ProjectRuntimeResolver.ResolveRootsAsync(db, command.Request.DeviceId, projectIds, ct);
        var mapped = rows.Select(row =>
        {
            string? root = null;
            if (row.ProjectId is { } projectId && CommandSandbox.IsProjectKind(row.Kind))
                roots.TryGetValue(projectId, out root);
            return EnqueueCommandHandler.MapCommand(row, root);
        }).ToList();
        return Result.Ok((IList<WorkstationCommandDto>)mapped);
    }
}
