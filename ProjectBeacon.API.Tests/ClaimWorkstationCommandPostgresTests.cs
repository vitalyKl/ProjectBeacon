namespace ProjectBeacon.API.Tests;

using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

[Collection("postgres-serial")]
public sealed class ClaimWorkstationCommandPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ClaimWorkstationCommandPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ConcurrentClaim_SingleCommand_OnlyOneWorkerWins()
    {
        if (!_postgres.Available) return;

        Guid deviceId;
        await using (var seed = _postgres.CreateContext())
        {
            var device = SeedDevice(seed, "atomic-single");
            await seed.SaveChangesAsync();
            deviceId = device.Id;
            seed.WorkstationCommands.Add(WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir));
            await seed.SaveChangesAsync();
        }

        var results = await Task.WhenAll(
            ClaimAsync(deviceId), ClaimAsync(deviceId), ClaimAsync(deviceId));

        var winners = results.Where(r => r.Success && r.Value is not null).ToList();
        var losers = results.Where(r => r.Success && r.Value is null).ToList();
        Assert.Single(winners);
        Assert.Equal(2, losers.Count);
        Assert.Equal(WorkstationCommandStatus.Running, winners[0].Value!.Status);
        Assert.NotNull(winners[0].Value!.StartedAt);
    }

    [Fact]
    public async Task ConcurrentClaim_LoserAdvancesToNextPending()
    {
        if (!_postgres.Available) return;

        Guid deviceId;
        await using (var seed = _postgres.CreateContext())
        {
            var device = SeedDevice(seed, "atomic-two");
            await seed.SaveChangesAsync();
            deviceId = device.Id;
            seed.WorkstationCommands.Add(WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir));
            seed.WorkstationCommands.Add(WorkstationCommand.Create(device.Id, WorkstationCommandKind.Probe));
            await seed.SaveChangesAsync();
        }

        var results = await Task.WhenAll(ClaimAsync(deviceId), ClaimAsync(deviceId));

        var claimed = results
            .Select(r => r.Value)
            .Where(v => v is not null)
            .ToList();
        Assert.Equal(2, claimed.Count);
        Assert.Equal(2, claimed.Select(c => c!.Id).Distinct().Count());
        Assert.All(claimed, c => Assert.Equal(WorkstationCommandStatus.Running, c!.Status));
    }

    [Fact]
    public async Task ClaimReturnsOldestFirstByCreatedAt()
    {
        if (!_postgres.Available) return;

        Guid deviceId;
        await using (var seed = _postgres.CreateContext())
        {
            var device = SeedDevice(seed, "fifo");
            await seed.SaveChangesAsync();
            deviceId = device.Id;

            var first = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir);
            var second = WorkstationCommand.Create(device.Id, WorkstationCommandKind.Probe);
            seed.WorkstationCommands.Add(first);
            seed.WorkstationCommands.Add(second);
            await seed.SaveChangesAsync();

            // Detach CreatedAt from insertion order: "first" is actually the newest.
            var baseTime = DateTime.UtcNow.AddMinutes(-5);
            await seed.WorkstationCommands
                .Where(c => c.Id == first.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, baseTime.AddMinutes(2)));
            await seed.WorkstationCommands
                .Where(c => c.Id == second.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, baseTime));
            await seed.SaveChangesAsync();
        }

        var oldest = await ClaimAsync(deviceId);
        var next = await ClaimAsync(deviceId);

        Assert.True(oldest.Success);
        Assert.True(next.Success);
        Assert.NotEqual(oldest.Value!.Id, next.Value!.Id);
        Assert.True(oldest.Value!.CreatedAt < next.Value!.CreatedAt);
    }

    [Fact]
    public async Task ClaimIsIsolatedByDevice()
    {
        if (!_postgres.Available) return;

        Guid deviceA, deviceB;
        await using (var seed = _postgres.CreateContext())
        {
            var a = SeedDevice(seed, "iso-a");
            var b = SeedDevice(seed, "iso-b");
            await seed.SaveChangesAsync();
            deviceA = a.Id;
            deviceB = b.Id;
            seed.WorkstationCommands.Add(WorkstationCommand.Create(a.Id, WorkstationCommandKind.ListDir));
            seed.WorkstationCommands.Add(WorkstationCommand.Create(b.Id, WorkstationCommandKind.Probe));
            await seed.SaveChangesAsync();
        }

        var claimed = await ClaimAsync(deviceA);

        Assert.True(claimed.Success);
        Assert.NotNull(claimed.Value);
        Assert.Equal(deviceA, claimed.Value!.DeviceId);
    }

    [Fact]
    public async Task ProjectKindWithoutRuntime_IsFailedNotClaimed()
    {
        if (!_postgres.Available) return;

        Guid deviceId, commandId;
        await using (var seed = _postgres.CreateContext())
        {
            var device = SeedDevice(seed, "runtime-missing");
            await seed.SaveChangesAsync();
            deviceId = device.Id;
            var command = WorkstationCommand.Create(
                device.Id, WorkstationCommandKind.InitProject, null, Guid.NewGuid(), null);
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync();
            commandId = command.Id;
        }

        var claimed = await ClaimAsync(deviceId);

        Assert.True(claimed.Success);
        Assert.Null(claimed.Value);

        await using var check = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await check.WorkstationCommands
                .AsNoTracking()
                .SingleAsync(c => c.Id == commandId);
            Assert.Equal(WorkstationCommandStatus.Failed, row.Status);
            Assert.Equal(CommandSandbox.RuntimeRequired, row.Error);
        }
    }

    private DaemonDevice SeedDevice(BeaconDbContext seed, string suffix)
    {
        var user = User.Create($"wsc-{suffix}", $"wsc-{suffix}@beacon.local", "hash");
        seed.Users.Add(user);
        var device = DaemonDevice.Create(
            $"device-{suffix}", user.Id, $"fp-{suffix}", $"token-hash-{suffix}", "bcd_test");
        seed.DaemonDevices.Add(device);
        return device;
    }

    private async Task<Application.Common.Result<WorkstationCommandDto?>> ClaimAsync(Guid deviceId)
    {
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ClaimNextCommandHandler(_postgres.CreateFactory());
            return await handler.HandleAsync(
                new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.Zero)));
        }
    }
}
