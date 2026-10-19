namespace ProjectBeacon.Infrastructure.Security;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectBeacon.Infrastructure.Data;

/// <summary>
/// Wires the Data Protection key ring to the Postgres DataProtectionKeys table, sealed
/// with key material derived from DATA_PROTECTION_KEY. Repository and encryptor are
/// applied through a lazily run KeyManagementOptions configurer so the decision is made
/// against the final container; hosts whose database is not Postgres-backed keep the
/// framework's default ring.
/// </summary>
public static class DataProtectionExtensions
{
    public static IServiceCollection AddBeaconDataProtection(
        this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        services.AddDataProtection();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(
            sp => new DataProtectionRingConfigurator(sp, configuration, environmentName));
        return services;
    }

    /// <summary>
    /// Creates a key when the ring is empty, by running the runtime's own lazy key
    /// creation through a throwaway protector. A Postgres advisory lock serializes
    /// concurrent first starts, so exactly one key row appears.
    /// </summary>
    public static async Task SeedPrimaryKeyAsync(this IServiceProvider services)
    {
        const string lockName = "beacon:dp-ring-seed";
        await using var connection = new NpgsqlConnection(PostgresConnection.Resolve(services.GetRequiredService<IConfiguration>()));
        await connection.OpenAsync();
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_lock(hashtext('{lockName}'));", connection))
            await lockCommand.ExecuteNonQueryAsync();
        try
        {
            var stored = GetRingRepository(services).GetAllElements();
            if (stored.Count(element => element.Name.LocalName == "key") == 0)
            {
                var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("beacon-primary-seed");
                protector.Protect(string.Empty);
            }
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand($"SELECT pg_advisory_unlock(hashtext('{lockName}'));", connection);
            await unlockCommand.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Fails closed when the ring is unreadable with the configured key material (wrong
    /// key, tampered rows) or, when <paramref name="requireKeys"/>, when it is empty.
    /// </summary>
    public static void AssertRingHealthy(this IServiceProvider services, bool requireKeys = false)
    {
        var storedKeys = GetRingRepository(services)
            .GetAllElements()
            .Count(element => element.Name.LocalName == "key");
        if (requireKeys && storedKeys == 0)
            throw new InvalidOperationException("Data Protection key ring is empty.");

        var manager = services.GetRequiredService<IKeyManager>();
        var loaded = manager.GetAllKeys().ToList();
        foreach (var key in loaded)
        {
            try
            {
                _ = key.Descriptor;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Data Protection key ring cannot be read with the configured key (wrong key or corrupted data).", ex);
            }
        }

        if (storedKeys > loaded.Count)
            throw new InvalidOperationException(
                $"Data Protection key ring holds {storedKeys} keys but only {loaded.Count} are readable.");
    }

    private static IXmlRepository GetRingRepository(IServiceProvider services) =>
        services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
            ?? throw new InvalidOperationException("The Data Protection XML repository is not configured.");
}
