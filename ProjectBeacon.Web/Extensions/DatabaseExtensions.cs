namespace ProjectBeacon.Web.Extensions;

using Application.Security;
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

    /// <summary>
    /// Re-encrypts remaining v1 ciphertexts to the v2 format after schema migration. Runs unscoped
    /// (bootstrap path) and reports migrated / remaining v1 / failed separately.
    /// </summary>
    public static async Task ReEncryptDataKeys(this WebApplication app)
    {
        var dbFactory = app.Services.GetRequiredService<Application.Persistence.IBeaconDbFactory>();
        var secrets = app.Services.GetRequiredService<ISecretProtector>();
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var result = await DataKeyMigration.ReEncryptAsync(db, secrets);
        logger.LogInformation(
            "Data key migration: migrated={Migrated} remainingV1={RemainingV1} failed={Failed}",
            result.Migrated, result.RemainingV1, result.Failed);
        if (!result.LegacyKeyAvailable && result.RemainingV1 > 0)
        {
            logger.LogWarning(
                "Data key migration: v1-to-v2 transfer is not possible because no legacy key material " +
                "is configured; {RemainingV1} v1 ciphertext(s) stay unreadable until one is provided.",
                result.RemainingV1);
        }
        if (result.Failed > 0)
        {
            logger.LogWarning(
                "Data key migration: {Failed} row(s) failed to decrypt and keep their original v1 ciphertext.",
                result.Failed);
        }
    }
}
