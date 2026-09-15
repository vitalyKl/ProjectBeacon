namespace ProjectBeacon.Application.Tests;

using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class HandlerSqlite
{
    public static (SqliteConnection Connection, BeaconDbContext Db, IDisposable Unscoped) Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(connection).Options);
        var unscoped = TenantScope.EnterUnscoped();
        db.Database.EnsureCreated();
        return (connection, db, unscoped);
    }

    public static IDbContextFactory<BeaconDbContext> Factory(SqliteConnection connection, ITenantContext? tenant = null)
        => new BeaconDbFactory(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(connection).Options, tenant);
}
