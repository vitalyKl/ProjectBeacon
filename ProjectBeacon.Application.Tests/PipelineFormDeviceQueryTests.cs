namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;

public sealed class PipelineFormDeviceQueryTests
{
    [Fact]
    public async Task ActiveDevicePredicate_TranslatesUsingMappedRevokedAtColumn()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        using (unscoped)
        {
            var user = User.Create("form-query-user", "form-query-user@example.test", "test-hash");
            var active = DaemonDevice.Create("active", user.Id, "active-fingerprint", "active-token-hash", "active");
            var revoked = DaemonDevice.Create("revoked", user.Id, "revoked-fingerprint", "revoked-token-hash", "revoked");
            revoked.Revoke();

            db.Users.Add(user);
            db.DaemonDevices.AddRange(active, revoked);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var matches = await db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == active.Id
                    && device.UserId == user.Id
                    && device.RevokedAt == null)
                .Select(device => device.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Single(matches);
            Assert.Equal(active.Id, matches[0]);
        }
    }
}
