namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Auth;
using Application.Common;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
            device.SetDesiredWorkstation(stored);
            payloadJson = DesiredState.WithRevision(stored, device.DesiredRevision);
        }
        if (CommandSandbox.IsProjectKind(command.Request.Kind))
        {
            if (command.Request.ProjectId is not { } rootedProjectId)
                return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired);
            var resolved = await ProjectRuntimeResolver.ResolveAsync(db, rootedProjectId, device.Id, ct);
            if (!resolved.Success)
                return Result.Failure<WorkstationCommandDto>(resolved.Error ?? CommandSandbox.RuntimeRequired);
            var runtime = resolved.Value!;
            var sanitized = CommandSandbox.SanitizeProjectPayload(payloadJson);
            if (!sanitized.Success)
                return Result.Failure<WorkstationCommandDto>(sanitized.Error ?? "Invalid payload.");
            payloadJson = sanitized.Value;
            localRoot = runtime.LocalRoot;
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
            command.Version);
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

            // Guarded claim: succeeds only if the row is still pending.
            var startedAt = DateTime.UtcNow;
            var claimed = await db.WorkstationCommands
                .Where(c => c.Id == next.Id && c.Status == WorkstationCommandStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, WorkstationCommandStatus.Running)
                    .SetProperty(c => c.StartedAt, startedAt), ct);
            if (claimed != 1)
                continue; // lost the race; the next pending command (if any) is picked up next iteration.

            return Result.Ok<WorkstationCommandDto?>(new WorkstationCommandDto(
                next.Id, next.DeviceId, next.ProjectId, next.RequestedByUserId, next.Kind,
                WorkstationCommandStatus.Running, next.PayloadJson, next.ResultJson, null,
                next.CreatedAt, startedAt, null, localRoot, next.Version));
        }
    }
}
/// <summary>
/// Marks a device command succeeded or failed.
/// </summary>
public class CompleteCommandHandler : ICommandHandler<CompleteCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CompleteCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(CompleteCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null || row.DeviceId != command.Request.DeviceId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        try
        {
            if (command.Request.Success)
            {
                await CommandCompletions.ApplyAsync(db, row, true, command.Request.ResultJson, null, ct);
                row.Succeed(command.Request.ResultJson);
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(command.Request.Error) ? "Command failed." : command.Request.Error;
                await CommandCompletions.ApplyAsync(db, row, false, command.Request.ResultJson, error, ct);
                row.Fail(error);
            }
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
