namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Mail;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Email;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public sealed class PlatformEmailAdminServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly AesSecretProtector _protector = TestProtectors.Dev();
    private readonly CapturingEmailSender _email = new();

    public PlatformEmailAdminServiceTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _dbFactory = HandlerSqlite.Factory(_connection);
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Save_EncryptsPassword_AndReadNeverReturnsIt()
    {
        var service = CreateService();
        var saved = await service.SaveAsync(
            new SavePlatformEmailSettingsRequest(
                true, "smtp.example.test", 587, "beacon", "beacon@example.test", true, "smtp-secret", false),
            AdminActor());

        Assert.True(saved.Enabled);
        Assert.True(saved.HasPassword);
        Assert.Equal("database", saved.Source);

        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        var row = await db.PlatformEmailSettings.SingleAsync(
            item => item.Id == PlatformEmailSettings.SingletonId,
            TestContext.Current.CancellationToken);
        Assert.StartsWith(ISecretProtector.V2Prefix, row.PasswordCipher);
        Assert.Equal("smtp-secret", _protector.Open(row.PasswordCipher));
        Assert.DoesNotContain("smtp-secret", System.Text.Json.JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task BlankPasswordPreservesExistingCredential_AndClearPasswordRemovesIt()
    {
        var service = CreateService();
        await service.SaveAsync(
            new SavePlatformEmailSettingsRequest(
                true, "smtp.example.test", 587, "beacon", "beacon@example.test", true, "secret-one", false),
            AdminActor());

        await service.SaveAsync(
            new SavePlatformEmailSettingsRequest(
                true, "smtp.example.test", 587, "beacon", "beacon@example.test", true, "", false),
            AdminActor());

        var tenant = GlobalTenant();
        await using (var db = _dbFactory.CreateDbContext(tenant))
        {
            var row = await db.PlatformEmailSettings.SingleAsync(
                item => item.Id == PlatformEmailSettings.SingletonId,
                TestContext.Current.CancellationToken);
            Assert.Equal("secret-one", _protector.Open(row.PasswordCipher));
        }

        await service.SaveAsync(
            new SavePlatformEmailSettingsRequest(
                true, "smtp.example.test", 587, "beacon", "beacon@example.test", true, "", true),
            AdminActor());

        await using var verifyDb = _dbFactory.CreateDbContext(tenant);
        var cleared = await verifyDb.PlatformEmailSettings.SingleAsync(
            item => item.Id == PlatformEmailSettings.SingletonId,
            TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, cleared.PasswordCipher);
        Assert.False((await service.GetAsync()).HasPassword);
    }

    [Fact]
    public async Task EnvironmentFallback_AuthenticatedPort25StillRequiresStartTls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAIL:Host"] = "smtp.example.test",
                ["MAIL:Port"] = "25",
                ["MAIL:User"] = "beacon",
                ["MAIL:Password"] = "secret",
                ["MAIL:From"] = "beacon@example.test"
            })
            .Build();
        var service = new PlatformEmailAdminService(
            _dbFactory, _protector, _email, configuration);

        var settings = await service.GetAsync();

        Assert.Equal("environment", settings.Source);
        Assert.True(settings.Enabled);
        Assert.True(settings.UseStartTls);
        Assert.True(settings.HasPassword);
    }

    [Fact]
    public async Task ConnectionCheckAndTestMessageUseSeparateActions()
    {
        var service = CreateService();
        await service.SaveAsync(
            new SavePlatformEmailSettingsRequest(
                true, "smtp.example.test", 587, "beacon", "beacon@example.test", true, "smtp-secret", false),
            AdminActor());

        await service.TestConnectionAsync(AdminActor());
        await service.SendTestAsync("operator@example.test", AdminActor());

        Assert.Equal(1, _email.ConnectionTests);
        Assert.Single(_email.Sent);
        Assert.Equal("ProjectBeacon SMTP test", _email.Sent[0].Subject);
    }

    [Fact]
    public async Task RecentDeliveryHistoryContainsOnlyMetadata()
    {
        var service = CreateService();
        _seedDb.EmailDeliveryLogs.Add(EmailDeliveryLog.Create(
            DateTime.UtcNow, "operator@example.test", "smtp_test", "Sent"));
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var history = await service.ListRecentAsync();
        Assert.Single(history);
        Assert.Equal("operator@example.test", history[0].Recipient);
        Assert.Equal("smtp_test", history[0].Purpose);
        Assert.Equal("Sent", history[0].Status);
    }

    private PlatformEmailAdminService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new PlatformEmailAdminService(_dbFactory, _protector, _email, configuration);
    }

    private static ActorContext AdminActor() =>
        new(
            ActorType.Human,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            true,
            null,
            null,
            null,
            null,
            ApiTokenCapability.None);

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }
}
