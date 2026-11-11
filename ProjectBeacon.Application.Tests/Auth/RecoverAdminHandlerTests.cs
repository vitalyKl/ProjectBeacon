namespace ProjectBeacon.Application.Tests;

using Application.Auth;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

public sealed class RecoverAdminHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly BcryptPasswordHasher _passwords = new();

    public RecoverAdminHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Recovery_ResetsPasswordClearsAuthenticatorAndInvalidatesSessions()
    {
        var admin = User.Create("admin", "admin@example.test", "old-password-hash", isAdmin: true);
        admin.BeginTotp("protected-totp-secret");
        admin.ConfirmTotp();
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var initialSessionVersion = admin.SessionVersion;

        var sessionVersions = new RecordingSessionVersionCache();
        var result = await CreateHandler(sessionVersions).HandleAsync("deployment-break-glass-token",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal(admin.Id, result.Value.UserId);
        Assert.True(result.Value.AuthenticatorReset);
        Assert.Equal(32, result.Value.Password.Length);
        Assert.Equal(admin.Id, sessionVersions.InvalidatedUserId);

        await _db.Entry(admin).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(initialSessionVersion + 1, admin.SessionVersion);
        Assert.False(admin.TotpEnabled);
        Assert.Equal(string.Empty, admin.TotpSecretCipher);
        Assert.Equal("SystemAdministrator", admin.OperatorRole);
        Assert.NotEqual("old-password-hash", admin.PasswordHash);
    }

    [Fact]
    public async Task Recovery_DoesNotResetAnOrdinaryAccountNamedAdmin()
    {
        var ordinary = User.Create("admin", "admin@example.test", "unchanged-password-hash");
        _db.Users.Add(ordinary);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var initialSessionVersion = ordinary.SessionVersion;

        var result = await CreateHandler().HandleAsync("deployment-break-glass-token",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        await _db.Entry(ordinary).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("unchanged-password-hash", ordinary.PasswordHash);
        Assert.Equal(initialSessionVersion, ordinary.SessionVersion);
    }

    [Fact]
    public async Task Recovery_WithInvalidTokenLeavesAuthenticatorAndPasswordUntouched()
    {
        var admin = User.Create("admin", "admin@example.test", "old-password-hash", isAdmin: true);
        admin.BeginTotp("protected-totp-secret");
        admin.ConfirmTotp();
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var initialSessionVersion = admin.SessionVersion;

        var result = await CreateHandler().HandleAsync("wrong-token",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        await _db.Entry(admin).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old-password-hash", admin.PasswordHash);
        Assert.Equal(initialSessionVersion, admin.SessionVersion);
        Assert.True(admin.TotpEnabled);
        Assert.Equal("protected-totp-secret", admin.TotpSecretCipher);
    }

    [Fact]
    public async Task Recovery_BootstrapSecretCannotAuthorizeRecovery()
    {
        var admin = User.Create("admin", "admin@example.test", "old-password-hash", isAdmin: true);
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler().HandleAsync("bootstrap-only-secret",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        await _db.Entry(admin).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old-password-hash", admin.PasswordHash);
    }

    [Fact]
    public async Task Recovery_WithoutDedicatedSecretFailsClosed()
    {
        var admin = User.Create("admin", "admin@example.test", "old-password-hash", isAdmin: true);
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateHandler(recoveryToken: null).HandleAsync(
            "bootstrap-only-secret", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        await _db.Entry(admin).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old-password-hash", admin.PasswordHash);
    }

    private RecoverAdminHandler CreateHandler(
        IUserSessionVersionCache? sessionVersions = null,
        string? recoveryToken = "dedicated-recovery-secret")
    {
        var values = new Dictionary<string, string?>();
        if (recoveryToken is not null)
            values["ADMIN_RECOVERY_TOKEN"] = recoveryToken;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new RecoverAdminHandler(_factory, _passwords, configuration,
            sessionVersions: sessionVersions);
    }

    private sealed class RecordingSessionVersionCache : IUserSessionVersionCache
    {
        public Guid? InvalidatedUserId { get; private set; }

        public bool TryGet(Guid userId, out UserSessionVersionSnapshot? snapshot)
        {
            snapshot = null;
            return false;
        }

        public void Set(Guid userId, UserSessionVersionSnapshot snapshot) { }

        public void Invalidate(Guid userId) => InvalidatedUserId = userId;
    }
}
