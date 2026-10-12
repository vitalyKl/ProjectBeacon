namespace ProjectBeacon.Web.Extensions;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Infrastructure.Data;
/// <summary>
/// Applies pending EF migrations on the Web host as the superuser migration role, then
/// bootstraps the non-superuser runtime role that the application connects as.
/// </summary>
public static class DatabaseExtensions
{
    public static async Task MigrateDatabase(this WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var connectionString = PostgresConnection.ResolveMigration(configuration);
        var options = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(connectionString).Options;
        await using var context = new BeaconDbContext(options);
        await context.Database.MigrateAsync();
        await PostgresConnection.EnsureAppRoleAsync(configuration);
    }
}
