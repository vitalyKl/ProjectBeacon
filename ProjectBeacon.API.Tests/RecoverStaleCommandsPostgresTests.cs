namespace ProjectBeacon.API.Tests;

using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

[Collection("postgres-serial")]
public sealed class RecoverStaleCommandsPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public RecoverStaleCommandsPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ConcurrentRecovery_OnlyOneTickWins()
    {
        _postgres.RequirePostgres();

        Guid commandId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-user", "rc-user@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc", user.Id, "fp-rc", "token-hash", "bcd_test");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir);
            command.Claim(TimeSpan.FromSeconds(30), "inst-old");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
        }

        var now = DateTime.UtcNow.AddSeconds(46);
        var results = await Task.WhenAll(RecoverAsync(now), RecoverAsync(now), RecoverAsync(now));

        Assert.Equal(1, results.Sum(r => r));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.LeaseExpiresAt);
        }
    }

    [Fact]
    public async Task RecoveryFailure_TransitionsExactlyOnce()
    {
        _postgres.RequirePostgres();

        Guid commandId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-fail-user", "rc-fail-user@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc-fail", user.Id, "fp-rc-fail", "token-hash-fail", "bcd_test");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ChatPrompt, "{}");
            command.Claim(TimeSpan.FromSeconds(30), "inst-old");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
        }

        var now = DateTime.UtcNow.AddSeconds(46);
        var results = await Task.WhenAll(RecoverAsync(now), RecoverAsync(now));

        Assert.Equal(1, results.Sum(r => r));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
            Assert.Equal(CommandRecovery.AtMostOnceExpiredError, row.Error);
        }
    }

    private Task<int> RecoverAsync(DateTime now)
    {
        return Task.Run(async () =>
        {
            using var scope = TenantScope.EnterUnscoped();
            await using var db = _postgres.CreateContext();
            return await CommandRecovery.RecoverAsync(db, now, default);
        });
    }

    // Moves the device's last heartbeat to a model time, keeping the daemon "alive" at a
    // future `now` without sleeping. Mirrors a real heartbeat: the instance id is the one
    // the claiming loop uses, so tier 1 (instance mismatch) stays off.
    private Task SetHeartbeatAsync(Guid deviceId, string instanceId, DateTime beat)
    {
        return Task.Run(async () =>
        {
            using var scope = TenantScope.EnterUnscoped();
            await using var db = _postgres.CreateContext();
            await db.DaemonDevices
                .Where(d => d.Id == deviceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LastHeartbeatAt, beat)
                    .SetProperty(d => d.ActiveInstanceId, instanceId), default);
        });
    }

    // Long-stale tier: a live daemon (same process instance, fresh heartbeat) with a lost
    // completion. The row is untouched inside the lease grace and recovered past it.

    [Fact]
    public async Task LiveDaemon_LongStale_Idempotent_IsRecoveredAfterGraceOnly()
    {
        _postgres.RequirePostgres();

        Guid commandId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-live-user", "rc-live@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc-live", user.Id, "fp-rc-live", "token-hash-live", "bcd_test");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir);
            command.Claim(TimeSpan.FromSeconds(30), "inst-1");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
            deviceId = device.Id;
        }

        // Inside the grace: the daemon is alive and still the same instance — no recovery.
        var insideGrace = DateTime.UtcNow.AddMinutes(16);
        await SetHeartbeatAsync(deviceId, "inst-1", insideGrace.AddSeconds(-10));
        Assert.Equal(0, await RecoverAsync(insideGrace));

        // Past the grace: the long-stale tier takes the lost command.
        var pastGrace = DateTime.UtcNow.AddMinutes(31);
        await SetHeartbeatAsync(deviceId, "inst-1", pastGrace.AddSeconds(-10));
        Assert.Equal(1, await RecoverAsync(pastGrace));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.LeaseExpiresAt);
        }
    }

    [Fact]
    public async Task LiveDaemon_LongStale_ConcurrentTicks_OnlyOneWins()
    {
        _postgres.RequirePostgres();

        Guid commandId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-live-race-user", "rc-live-race@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc-live-race", user.Id, "fp-rc-live-race", "token-hash-live-race", "bcd_test");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir);
            command.Claim(TimeSpan.FromSeconds(30), "inst-1");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
            deviceId = device.Id;
        }

        var now = DateTime.UtcNow.AddMinutes(31);
        await SetHeartbeatAsync(deviceId, "inst-1", now.AddSeconds(-10));

        var results = await Task.WhenAll(RecoverAsync(now), RecoverAsync(now), RecoverAsync(now));
        Assert.Equal(1, results.Sum(r => r));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.LeaseExpiresAt);
        }
    }

    [Fact]
    public async Task LiveDaemon_LongStale_AtMostOnce_FailedAndLateCompletionRejected()
    {
        _postgres.RequirePostgres();

        Guid commandId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-live-atmo-user", "rc-live-atmo@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc-live-atmo", user.Id, "fp-rc-live-atmo", "token-hash-live-atmo", "bcd_test");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ChatPrompt, "{}");
            command.Claim(TimeSpan.FromSeconds(30), "inst-1");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
            deviceId = device.Id;
        }

        var now = DateTime.UtcNow.AddMinutes(31);
        await SetHeartbeatAsync(deviceId, "inst-1", now.AddSeconds(-10));
        Assert.Equal(1, await RecoverAsync(now));

        // Late completions from the lost attempt: neither the legacy null generation nor the
        // first-attempt generation may finish or affect a terminal row.
        foreach (var generation in new int?[] { null, 1 })
        {
            var result = await new CompleteCommandHandler(_postgres.CreateFactory()).HandleAsync(
                new CompleteCommandCommand(new CompleteCommandRequest(commandId, deviceId, true, "{}", null, generation)), TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Equal("Command already finished.", result.Error);
        }

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
            Assert.Equal(CommandRecovery.AtMostOnceExpiredError, row.Error);
        }
    }

    [Fact]
    public async Task LiveDaemon_LongStale_Requeued_CompletesOnce_DuplicateRejected()
    {
        _postgres.RequirePostgres();

        Guid commandId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("rc-live-repro-user", "rc-live-repro@beacon.local", "hash");
            seed.Users.Add(user);
            var device = DaemonDevice.Create("device-rc-live-repro", user.Id, "fp-rc-live-repro", "token-hash-live-repro", "bcd_test");
            device.BumpDesired();
            device.BumpDesired();
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ReconcileDesired, DesiredState.WithRevision("{}", 2));
            command.Claim(TimeSpan.FromSeconds(30), "inst-1");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            commandId = command.Id;
            deviceId = device.Id;
        }

        var now = DateTime.UtcNow.AddMinutes(31);
        await SetHeartbeatAsync(deviceId, "inst-1", now.AddSeconds(-10));
        Assert.Equal(1, await RecoverAsync(now));

        // The live daemon re-claims the row under the new generation and completes it.
        var factory = _postgres.CreateFactory();
        var claimed = await new ClaimNextCommandHandler(factory).HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        Assert.True(claimed.Success, claimed.Error);
        Assert.Equal(commandId, claimed.Value!.Id);
        Assert.Equal(2, claimed.Value!.Generation);

        var first = await new CompleteCommandHandler(factory).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(commandId, deviceId, true, DesiredState.WithRevision("{}", 2), null, 2)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // A duplicate or late completion of the same attempt applies nothing.
        var duplicate = await new CompleteCommandHandler(factory).HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(commandId, deviceId, true, DesiredState.WithRevision("{}", 2), null, 2)), TestContext.Current.CancellationToken);
        Assert.False(duplicate.Success);
        Assert.Equal("Command already finished.", duplicate.Error);

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Succeeded, row.Status);
            var device = await check.DaemonDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(2, device.AppliedRevision);
        }
    }
}
