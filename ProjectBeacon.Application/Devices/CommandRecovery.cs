namespace ProjectBeacon.Application.Devices;

using Domain.Entities.Devices;
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
    /// Long-stale tier, in minutes: a running command whose lease expired more than this long ago
    /// is recovered even when the claiming process is still alive and heartbeating. The 15-minute
    /// lease already far exceeds every client-side execution cap (LLM/eval 180s, review/winget
    /// 120s, the rest seconds), and the client's bounded completion retries finish within seconds
    /// of the execution — so a lease expired by this much can only belong to a lost attempt.
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
    public static async Task<int> RecoverAsync(IBeaconDb db, DateTime now, CancellationToken ct)
    {
        var candidates = await StaleRows(db, now).OrderBy(c => c.CreatedAt).ToListAsync(ct);

        var recovered = 0;
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
            }
        }
        await db.SaveChangesAsync(ct);
        return recovered;
    }
}
