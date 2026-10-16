namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Devices;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CommandRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public CommandRecoveryTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    // Time model: T0 is when the device heartbeats and the command is claimed with a 30s lease.
    // T0+31s: lease expired, device still inside its 45s online window.
    // T0+46s: lease expired and device offline.
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private async Task<(DaemonDevice Device, DateTime T0)> SeedOnlineDeviceAsync(string? instanceId)
    {
        var t0 = DateTime.UtcNow;
        using var scope = TenantScope.EnterUnscoped();
        var user = User.Create("dev-" + Guid.NewGuid().ToString("N"), "dev@beacon.local", "hash");
        _db.Users.Add(user);
        var device = DaemonDevice.Create("laptop", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash", "bcd_test");
        device.Heartbeat(instanceId: instanceId);
        _db.DaemonDevices.Add(device);
        await _db.SaveChangesAsync();
        return (device, t0);
    }

    private async Task<WorkstationCommand> SeedRunningAsync(
        DaemonDevice device, WorkstationCommandKind kind, string? payload, string? instanceId)
    {
        using var scope = TenantScope.EnterUnscoped();
        var command = WorkstationCommand.Create(device.Id, kind, payload, null, device.UserId);
        command.Claim(Lease, instanceId);
        _db.WorkstationCommands.Add(command);
        await _db.SaveChangesAsync();
        return command;
    }

    private async Task<WorkstationCommand> SnapshotAsync(Guid id)
    {
        using var scope = TenantScope.EnterUnscoped();
        return await _db.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    // Moves the device's last heartbeat to a model time, keeping the daemon "alive" at a
    // future `now` without sleeping.
    private async Task SetHeartbeatAsync(Guid deviceId, DateTime beat)
    {
        using var scope = TenantScope.EnterUnscoped();
        await _db.DaemonDevices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastHeartbeatAt, beat), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Decide_EncodesTheRecoveryRule()
    {
        Assert.Equal(CommandRecovery.RecoveryAction.None, CommandRecovery.Decide(WorkstationCommandKind.ListDir, 0, stale: false));
        Assert.Equal(CommandRecovery.RecoveryAction.Fail, CommandRecovery.Decide(WorkstationCommandKind.ChatPrompt, 0, stale: true));
        Assert.Equal(CommandRecovery.RecoveryAction.Fail, CommandRecovery.Decide(WorkstationCommandKind.ChatPrompt, 3, stale: true));
        Assert.Equal(CommandRecovery.RecoveryAction.Requeue, CommandRecovery.Decide(WorkstationCommandKind.ListDir, 0, stale: true));
        Assert.Equal(CommandRecovery.RecoveryAction.Fail, CommandRecovery.Decide(WorkstationCommandKind.ListDir, 1, stale: true));
    }

    [Fact]
    public async Task StaleIdempotent_IsRequeuedOnce()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Null(row.StartedAt);
    }

    [Fact]
    public async Task StaleAtMostOnce_IsFailedNeverRequeued()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ChatPrompt, "{}", "inst-1");

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
        Assert.Equal(CommandRecovery.AtMostOnceExpiredError, row.Error);
        Assert.Equal(0, row.Attempts);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task StaleIdempotentAfterRequeue_IsFailed()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken));

        _db.ChangeTracker.Clear();
        using var scope = TenantScope.EnterUnscoped();
        var row = await _db.WorkstationCommands.SingleAsync(c => c.Id == command.Id, TestContext.Current.CancellationToken);
        row.Claim(Lease, "inst-1");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(120), TestContext.Current.CancellationToken));

        var failed = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, failed.Status);
        Assert.Equal(CommandRecovery.RequeueLimitExpiredError, failed.Error);
    }

    [Fact]
    public async Task LeaseExpiredButDaemonLive_IsNotRecovered()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(31), TestContext.Current.CancellationToken);

        Assert.Equal(0, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Running, row.Status);
    }

    [Fact]
    public async Task RestartedDaemon_DifferentInstance_IsRecoveredWhileOnline()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");
        // The process restarted: same device, still heartbeating, new instance id.
        using var scope = TenantScope.EnterUnscoped();
        device.Heartbeat(instanceId: "inst-2");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(31), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
    }

    [Fact]
    public async Task RevokedDevice_IsRecovered()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ChatPrompt, "{}", "inst-1");
        using var scope = TenantScope.EnterUnscoped();
        device.Revoke();
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(31), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
    }

    [Fact]
    public async Task ChatPromptFailure_SetsStreamingSessionIdle()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var session = ChatSession.Create(Guid.NewGuid(), device.Id, "session", @"A:\repo");
        session.SetStreaming();
        using (var scope = TenantScope.EnterUnscoped())
        {
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var command = await SeedRunningAsync(
            device, WorkstationCommandKind.ChatPrompt,
            $$"""{"chatSessionId":"{{session.Id:N}}"}""", "inst-1");

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        Assert.Equal(WorkstationCommandStatus.Failed, (await SnapshotAsync(command.Id)).Status);
        using var check = TenantScope.EnterUnscoped();
        var fresh = await _db.ChatSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ChatSessionStatus.Idle, fresh.Status);
    }

    [Fact]
    public async Task RunEvalTurnFailure_FailsEvalRun()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var run = EvalRun.Create(Guid.NewGuid(), Guid.NewGuid(), null, EvalCondition.WithBrief);
        using (var scope = TenantScope.EnterUnscoped())
        {
            _db.EvalRuns.Add(run);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var command = await SeedRunningAsync(
            device, WorkstationCommandKind.RunEvalTurn,
            $$"""{"evalRunId":"{{run.Id:N}}"}""", "inst-1");

        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        using var check = TenantScope.EnterUnscoped();
        var fresh = await _db.EvalRuns.AsNoTracking().SingleAsync(e => e.Id == run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fresh.CompletedAt);
        Assert.False(fresh.Passed);
        Assert.Contains("expired", fresh.CheckOutput);
    }

    [Fact]
    public async Task Complete_LegacyNullGeneration_AcceptedOnFirstAttempt()
    {
        var (device, _) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        var completed = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, null)), TestContext.Current.CancellationToken);

        Assert.True(completed.Success, completed.Error);
        Assert.Equal(WorkstationCommandStatus.Succeeded, (await SnapshotAsync(command.Id)).Status);
    }

    [Fact]
    public async Task Complete_CurrentGeneration_AcceptedStaleRejected()
    {
        var (device, _) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        var stale = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, 99)), TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal("Command generation mismatch.", stale.Error);

        var current = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, 1)), TestContext.Current.CancellationToken);
        Assert.True(current.Success, current.Error);
    }

    [Fact]
    public async Task Complete_AfterRequeue_OldGenerationAndLegacyNullRejected()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");
        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken));

        var oldGeneration = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, 1)), TestContext.Current.CancellationToken);
        Assert.False(oldGeneration.Success);
        Assert.Equal("Command generation mismatch.", oldGeneration.Error);

        var legacy = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, null)), TestContext.Current.CancellationToken);
        Assert.False(legacy.Success);

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(device.Id, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.True(claimed.Success, claimed.Error);
        Assert.Equal(2, claimed.Value!.Generation);

        var current = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, 2)), TestContext.Current.CancellationToken);
        Assert.True(current.Success, current.Error);
        Assert.Equal(WorkstationCommandStatus.Succeeded, (await SnapshotAsync(command.Id)).Status);
    }

    [Fact]
    public async Task Claim_DtoCarriesGenerationAndGrantsLease()
    {
        var (device, _) = await SeedOnlineDeviceAsync("inst-1");
        using var scope = TenantScope.EnterUnscoped();
        var queued = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir, null, null, device.UserId);
        _db.WorkstationCommands.Add(queued);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(device.Id, TimeSpan.Zero)), TestContext.Current.CancellationToken);

        Assert.True(claimed.Success, claimed.Error);
        Assert.NotNull(claimed.Value);
        Assert.Equal(1, claimed.Value!.Generation);
        var row = await SnapshotAsync(queued.Id);
        Assert.Equal(WorkstationCommandStatus.Running, row.Status);
        Assert.NotNull(row.LeaseExpiresAt);
        Assert.Equal("inst-1", row.ClaimInstanceId);
    }

    [Fact]
    public async Task StaleReconcileDesired_IsRequeuedThenClaimedAgain_HeartbeatAddsNoDuplicate()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        using (var scope = TenantScope.EnterUnscoped())
        {
            device.BumpDesired();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ReconcileDesired, null, "inst-old");

        // The stuck Running row blocks nothing in the claim queue, but before recovery it can
        // never be re-claimed. Recovery re-queues it.
        var recovered = await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(31), TestContext.Current.CancellationToken);
        Assert.Equal(1, recovered);

        // A heartbeat from the live daemon must not enqueue a second ReconcileDesired:
        // the re-queued row is already in flight.
        var heartbeat = await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(device.Id, "{}", "{}", "inst-1")), TestContext.Current.CancellationToken);
        Assert.True(heartbeat.Success, heartbeat.Error);
        using var countScope = TenantScope.EnterUnscoped();
        var reconcileCount = await _db.WorkstationCommands
            .CountAsync(c => c.DeviceId == device.Id && c.Kind == WorkstationCommandKind.ReconcileDesired, TestContext.Current.CancellationToken);
        Assert.Equal(1, reconcileCount);

        // The live daemon then picks up the re-queued command under the new instance.
        var claimed = await new ClaimNextCommandHandler(Factory()).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(device.Id, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.True(claimed.Success, claimed.Error);
        Assert.Equal(command.Id, claimed.Value!.Id);
        Assert.Equal(2, claimed.Value!.Generation);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Running, row.Status);
        Assert.Equal("inst-1", row.ClaimInstanceId);
    }

    // Long-stale tier: the lease has been expired longer than CommandRecovery.LongStaleGraceMinutes
    // and the same process is still alive — a lost completion or a wedged loop, not a live run,
    // since every kind is hard-capped client-side far below the 15-minute lease.

    [Fact]
    public async Task LongStaleIdempotent_LiveDaemon_IsRequeued()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        var now = t0.AddSeconds(30).AddMinutes(31);
        await SetHeartbeatAsync(device.Id, now.AddSeconds(-10));

        var recovered = await CommandRecovery.RecoverAsync(_db, now, TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.LeaseExpiresAt);
        Assert.Null(row.StartedAt);
    }

    [Fact]
    public async Task LongStaleIdempotent_InsideGrace_LiveDaemon_IsNotRecovered()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        // The lease expired, but the long-stale grace has not passed: no proof of loss yet.
        var now = t0.AddSeconds(30).AddMinutes(29);
        await SetHeartbeatAsync(device.Id, now.AddSeconds(-10));

        var recovered = await CommandRecovery.RecoverAsync(_db, now, TestContext.Current.CancellationToken);

        Assert.Equal(0, recovered);
        Assert.Equal(WorkstationCommandStatus.Running, (await SnapshotAsync(command.Id)).Status);
    }

    [Fact]
    public async Task LongStaleAtMostOnce_LiveDaemon_IsFailedWithEffect()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var session = ChatSession.Create(Guid.NewGuid(), device.Id, "session", @"A:\repo");
        session.SetStreaming();
        using (var scope = TenantScope.EnterUnscoped())
        {
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var command = await SeedRunningAsync(
            device, WorkstationCommandKind.ChatPrompt,
            $$"""{"chatSessionId":"{{session.Id:N}}"}""", "inst-1");

        var now = t0.AddSeconds(30).AddMinutes(31);
        await SetHeartbeatAsync(device.Id, now.AddSeconds(-10));

        var recovered = await CommandRecovery.RecoverAsync(_db, now, TestContext.Current.CancellationToken);

        Assert.Equal(1, recovered);
        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
        Assert.Equal(CommandRecovery.AtMostOnceExpiredError, row.Error);
        Assert.Equal(0, row.Attempts);
        Assert.NotNull(row.CompletedAt);
        using var check = TenantScope.EnterUnscoped();
        var fresh = await _db.ChatSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ChatSessionStatus.Idle, fresh.Status);
    }

    [Fact]
    public async Task LongStaleAfterRequeue_LiveDaemon_IsFailedAtLimit()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ListDir, null, "inst-1");

        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken));

        _db.ChangeTracker.Clear();
        using var scope = TenantScope.EnterUnscoped();
        var row = await _db.WorkstationCommands.SingleAsync(c => c.Id == command.Id, TestContext.Current.CancellationToken);
        row.Claim(Lease, "inst-1");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The second attempt also wedges on the live daemon: the re-queue limit is exhausted.
        var now = t0.AddSeconds(30).AddMinutes(31);
        await SetHeartbeatAsync(device.Id, now.AddSeconds(-10));

        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, now, TestContext.Current.CancellationToken));

        var failed = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, failed.Status);
        Assert.Equal(CommandRecovery.RequeueLimitExpiredError, failed.Error);
    }

    [Fact]
    public async Task Complete_TerminalRow_LegacyNullAndOldGeneration_Rejected()
    {
        var (device, t0) = await SeedOnlineDeviceAsync("inst-1");
        var command = await SeedRunningAsync(device, WorkstationCommandKind.ChatPrompt, "{}", "inst-1");
        Assert.Equal(1, await CommandRecovery.RecoverAsync(_db, t0.AddSeconds(46), TestContext.Current.CancellationToken));

        // A late completion from the lost attempt must not finish a terminal row: neither the
        // legacy null generation nor the first-attempt generation may apply, and no effect runs.
        var legacy = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, null)), TestContext.Current.CancellationToken);
        Assert.False(legacy.Success);
        Assert.Equal("Command already finished.", legacy.Error);

        var oldGeneration = await new CompleteCommandHandler(Factory()).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(command.Id, device.Id, true, "{}", null, 1)), TestContext.Current.CancellationToken);
        Assert.False(oldGeneration.Success);
        Assert.Equal("Command already finished.", oldGeneration.Error);

        var row = await SnapshotAsync(command.Id);
        Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
        Assert.Equal(CommandRecovery.AtMostOnceExpiredError, row.Error);
    }
}
