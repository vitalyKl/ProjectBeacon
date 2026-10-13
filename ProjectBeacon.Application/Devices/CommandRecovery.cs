namespace ProjectBeacon.Application.Devices;

using Domain.Entities.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Recovers <see cref="WorkstationCommandStatus.Running"/> commands whose execution lease expired
/// while the claiming process is gone: the device is revoked, offline, or heartbeating from a
/// different process instance.
/// At-most-once kinds are failed and never re-queued (<see cref="CommandDelivery.IsAtMostOnce"/>).
/// Idempotent kinds are re-queued up to <see cref="MaxRequeues"/> times, then failed.
/// A live daemon with a wedged loop is not recovered: its claim instance still matches, so the
/// rule leaves it alone until the process restarts (new instance id) or the device goes offline.
/// </summary>
public static class CommandRecovery
{
    /// <summary>How many times an idempotent stale command may be re-queued before it is failed.</summary>
    public const int MaxRequeues = 1;

    public const string AtMostOnceExpiredError = "Command expired before completion; at-most-once commands are not re-queued.";
    public const string RequeueLimitExpiredError = "Command expired before completion; the re-queue limit was reached.";

    public enum RecoveryAction { None, Requeue, Fail }

    /// <summary>
    /// Pure decision. <paramref name="stale"/> means the lease is expired AND the device is
    /// revoked, offline, or heartbeating from a different process instance.
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
    /// Applies the recovery rule to every stale running command. Each transition is a guarded
    /// update that re-verifies the lease and the device state in the same statement, so
    /// concurrent ticks mutate a row at most once. On failure, the standard completion effects
    /// resolve dependent state (eval run, review run, chat session).
    /// Returns the number of recovered rows.
    /// </summary>
    public static async Task<int> RecoverAsync(IBeaconDb db, DateTime now, CancellationToken ct)
    {
        var offlineBefore = now - TimeSpan.FromSeconds(DaemonDevice.OnlineWindowSeconds);
        var candidates = await db.WorkstationCommands
            .Where(c => c.Status == WorkstationCommandStatus.Running
                && c.LeaseExpiresAt != null
                && c.LeaseExpiresAt < now
                && !db.DaemonDevices.Any(d => d.Id == c.DeviceId
                    && d.RevokedAt == null
                    && d.LastHeartbeatAt >= offlineBefore
                    && d.ActiveInstanceId == c.ClaimInstanceId))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var recovered = 0;
        foreach (var row in candidates)
        {
            var action = Decide(row.Kind, row.Attempts, stale: true);
            if (action == RecoveryAction.None)
                continue;

            if (action == RecoveryAction.Requeue)
            {
                var requeued = await db.WorkstationCommands
                    .Where(c => c.Id == row.Id
                        && c.Status == WorkstationCommandStatus.Running
                        && c.LeaseExpiresAt != null
                        && c.LeaseExpiresAt < now
                        && !db.DaemonDevices.Any(d => d.Id == c.DeviceId
                            && d.RevokedAt == null
                            && d.LastHeartbeatAt >= offlineBefore
                            && d.ActiveInstanceId == c.ClaimInstanceId))
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
            var updated = await db.WorkstationCommands
                .Where(c => c.Id == row.Id
                    && c.Status == WorkstationCommandStatus.Running
                    && c.LeaseExpiresAt != null
                    && c.LeaseExpiresAt < now
                    && !db.DaemonDevices.Any(d => d.Id == c.DeviceId
                        && d.RevokedAt == null
                        && d.LastHeartbeatAt >= offlineBefore
                        && d.ActiveInstanceId == c.ClaimInstanceId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, WorkstationCommandStatus.Failed)
                    .SetProperty(c => c.Error, error)
                    .SetProperty(c => c.CompletedAt, now)
                    .SetProperty(c => c.LeaseExpiresAt, (DateTime?)null), ct);
            if (updated == 1)
            {
                await CommandCompletions.ApplyAsync(db, row, false, null, error, ct);
                recovered++;
            }
        }
        await db.SaveChangesAsync(ct);
        return recovered;
    }
}
