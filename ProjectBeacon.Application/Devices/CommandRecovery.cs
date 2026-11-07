namespace ProjectBeacon.Application.Devices;

using Domain.Entities.Devices;
using Application.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Recovers <see cref="WorkstationCommandStatus.Running"/> commands whose execution lease expired.
/// Two tiers make a row stale:
/// <list type="bullet">
/// <item>the claiming process is gone — the device is revoked, offline, or heartbeating from a
/// different process instance;</item>
/// <item>the lease has been expired longer than <see cref="LongStaleGraceMinutes"/> — the
/// long-stale tier, which applies even when the same process is still alive: every kind is
/// hard-capped client-side, so at that point no legitimate execution can still be in flight
/// and a wedged loop or a lost acknowledgement is resolved instead of waiting for a restart.</item>
/// </list>
/// At-most-once kinds are failed and never re-queued (<see cref="CommandDelivery.IsAtMostOnce"/>).
/// Idempotent kinds are re-queued up to <see cref="MaxRequeues"/> times, then failed.
/// </summary>
public static class CommandRecovery
{
    /// <summary>How many times an idempotent stale command may be re-queued before it is failed.</summary>
    public const int MaxRequeues = 1;

    /// <summary>
    /// Long-stale tier, in minutes after the command-specific lease expires: a running command
    /// is recovered even when the claiming process is still alive and heartbeating. Pipeline turns
    /// have a 75-minute lease for a 60-minute client-side cap; other command kinds retain the
    /// 15-minute lease. The grace period resolves a lost acknowledgement or wedged command loop.
    /// </summary>
    public const int LongStaleGraceMinutes = 30;

    public const string AtMostOnceExpiredError = "Command expired before completion; at-most-once commands are not re-queued.";
    public const string RequeueLimitExpiredError = "Command expired before completion; the re-queue limit was reached.";

    public enum RecoveryAction { None, Requeue, Fail }

    /// <summary>
    /// Pure decision. <paramref name="stale"/> means the lease is expired AND either the claiming
    /// process is gone (device revoked, offline, or heartbeating from a different process
    /// instance) or the lease has been expired longer than <see cref="LongStaleGraceMinutes"/>.
    /// </summary>
    public static RecoveryAction Decide(WorkstationCommandKind kind, int attempts, bool stale)
    {
        if (!stale)
            return RecoveryAction.None;
        if (CommandDelivery.IsAtMostOnce(kind) || attempts >= MaxRequeues)
            return RecoveryAction.Fail;
        return RecoveryAction.Requeue;
    }

    public static string FailError(WorkstationCommandKind kind, int attempts) =>
        CommandDelivery.IsAtMostOnce(kind) ? AtMostOnceExpiredError : RequeueLimitExpiredError;

    /// <summary>
    /// The single stale predicate, shared by the candidate query and every guarded transition:
    /// the lease is expired AND either the claiming process is gone (device revoked, offline, or
    /// heartbeating from a different process instance) or the lease has been expired longer than
    /// <see cref="LongStaleGraceMinutes"/> (long-stale tier).
    /// </summary>
    private static IQueryable<WorkstationCommand> StaleRows(IBeaconDb db, DateTime now)
    {
        var offlineBefore = now - TimeSpan.FromSeconds(DaemonDevice.OnlineWindowSeconds);
        var longStaleBefore = now - TimeSpan.FromMinutes(LongStaleGraceMinutes);
        return db.WorkstationCommands
            .Where(c => c.Status == WorkstationCommandStatus.Running
                && c.LeaseExpiresAt != null
                && c.LeaseExpiresAt < now
                && (c.LeaseExpiresAt < longStaleBefore
                    || !db.DaemonDevices.Any(d => d.Id == c.DeviceId
                        && d.RevokedAt == null
                        && d.LastHeartbeatAt >= offlineBefore
                        && d.ActiveInstanceId == c.ClaimInstanceId)));
    }

    /// <summary>
    /// Applies the recovery rule to every stale running command. Each transition is a guarded
    /// update that re-verifies the full stale predicate in the same statement, so concurrent
    /// ticks mutate a row at most once. On failure, the standard completion effects
    /// resolve dependent state (eval run, review run, chat session).
    /// Returns the number of recovered rows.
    /// </summary>
    public static Task<int> RecoverAsync(
        IBeaconDb db,
        TimeProvider timeProvider,
        CancellationToken ct,
        PipelineActorQueueDispatcher? pipelineQueue = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return RecoverAsync(db, timeProvider.GetUtcNow().UtcDateTime, ct, pipelineQueue);
    }

    public static async Task<int> RecoverAsync(
        IBeaconDb db,
        DateTime now,
        CancellationToken ct,
        PipelineActorQueueDispatcher? pipelineQueue = null)
    {
        var candidates = await StaleRows(db, now).OrderBy(c => c.CreatedAt).ToListAsync(ct);

        var recovered = 0;
        var pipelineTaskIds = new HashSet<Guid>();
        foreach (var row in candidates)
        {
            var action = Decide(row.Kind, row.Attempts, stale: true);
            if (action == RecoveryAction.None)
                continue;

            if (action == RecoveryAction.Requeue)
            {
                var requeued = await StaleRows(db, now)
                    .Where(c => c.Id == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, WorkstationCommandStatus.Pending)
                        .SetProperty(c => c.Attempts, e => e.Attempts + 1)
                        .SetProperty(c => c.StartedAt, (DateTime?)null)
                        .SetProperty(c => c.Error, (string?)null)
                        .SetProperty(c => c.LeaseExpiresAt, (DateTime?)null), ct);
                if (requeued == 1)
                    recovered++;
                continue;
            }

            var error = FailError(row.Kind, row.Attempts);
            var updated = await StaleRows(db, now)
                .Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, WorkstationCommandStatus.Failed)
                    .SetProperty(c => c.Error, error)
                    .SetProperty(c => c.CompletedAt, now)
                    .SetProperty(c => c.LeaseExpiresAt, (DateTime?)null), ct);
            if (updated == 1)
            {
                await CommandCompletions.ApplyAsync(db, row, false, null, error, ct);
                recovered++;

                // The Worker can recover a lost daemon completion without going through
                // CompleteCommandHandler. Preserve the same queue-advance guarantee here.
                if (pipelineQueue is not null
                    && row.Kind == WorkstationCommandKind.RunPipelineSession
                    && row.ProjectId is { } projectId
                    && DevicePayloadReader.ReadGuid(row.PayloadJson, "sessionId") is { } sessionId)
                {
                    var taskId = await db.PipelineSessions.IgnoreQueryFilters()
                        .Where(s => s.Id == sessionId && s.ProjectId == projectId)
                        .Select(s => (Guid?)s.TaskId)
                        .FirstOrDefaultAsync(ct);
                    if (taskId is { } recoveredTaskId)
                        pipelineTaskIds.Add(recoveredTaskId);
                }
            }
        }
        await db.SaveChangesAsync(ct);

        // Dispatch only after all recovery effects are durable. Several expired commands
        // for the same task collapse into one idempotent queue advance.
        if (pipelineQueue is not null)
        {
            foreach (var taskId in pipelineTaskIds)
                await pipelineQueue.DispatchNextAsync(taskId, ct);
        }

        return recovered;
    }
}
