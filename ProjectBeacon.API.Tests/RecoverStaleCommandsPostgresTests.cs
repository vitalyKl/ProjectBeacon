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
        if (!_postgres.Available) return;

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
            await seed.SaveChangesAsync();
            commandId = command.Id;
        }

        var now = DateTime.UtcNow.AddSeconds(46);
        var results = await Task.WhenAll(RecoverAsync(now), RecoverAsync(now), RecoverAsync(now));

        Assert.Equal(1, results.Sum(r => r));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId);
            Assert.Equal(WorkstationCommandStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.LeaseExpiresAt);
        }
    }

    [Fact]
    public async Task RecoveryFailure_TransitionsExactlyOnce()
    {
        if (!_postgres.Available) return;

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
            await seed.SaveChangesAsync();
            commandId = command.Id;
        }

        var now = DateTime.UtcNow.AddSeconds(46);
        var results = await Task.WhenAll(RecoverAsync(now), RecoverAsync(now));

        Assert.Equal(1, results.Sum(r => r));

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId);
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
}
