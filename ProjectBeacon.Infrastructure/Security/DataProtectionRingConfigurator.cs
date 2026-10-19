namespace ProjectBeacon.Infrastructure.Security;

using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProjectBeacon.Infrastructure.Data;

/// <summary>
/// Points the Data Protection key ring at the Postgres DataProtectionKeys table when the
/// host's database is Postgres-backed; otherwise the framework's default ring stays in
/// place. Runs as a KeyManagementOptions configurer, so the decision is made against the
/// final service container and test hosts that swap the database later are unaffected.
/// The decryptor needs no registration: the runtime activates KeyRingXmlDecryptor by
/// assembly-qualified name with this same container.
/// </summary>
public sealed class DataProtectionRingConfigurator : IConfigureOptions<KeyManagementOptions>
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly string _environmentName;

    public DataProtectionRingConfigurator(IServiceProvider services, IConfiguration configuration, string environmentName)
    {
        _services = services;
        _configuration = configuration;
        _environmentName = environmentName;
    }

    public void Configure(KeyManagementOptions options)
    {
        if (!PostgresConnection.IsPostgresBacked(_services))
            return;

        var keys = DataKeyPolicy.ResolveDataKeys(_configuration, _environmentName);
        var databaseOptions = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(PostgresConnection.Resolve(_configuration)).Options;
        options.XmlRepository = new DataProtectionKeyRepository(databaseOptions);
        options.XmlEncryptor = new KeyRingXmlEncryptor(keys.Current);
    }
}
