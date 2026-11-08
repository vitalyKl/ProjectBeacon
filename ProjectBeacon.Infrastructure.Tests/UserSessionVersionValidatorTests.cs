namespace ProjectBeacon.Infrastructure.Tests;

using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Security;

public sealed class UserSessionVersionValidatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly BeaconDbFactory _factory;
    private readonly ServiceProvider _services;
    private readonly User _user;

    public UserSessionVersionValidatorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        _factory = new BeaconDbFactory(options, tenant);
        _seedDb = new BeaconDbContext(options, tenant);
        _seedDb.Database.EnsureCreated();

        _user = User.Create("operator", "operator@example.com", "hash");
        _seedDb.Users.Add(_user);
        _seedDb.SaveChanges();

        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton<IUserSessionVersionCache, UserSessionVersionCache>();
        services.AddSingleton<IBeaconDbFactory>(_factory);
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _seedDb.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CurrentAccountState_AllowsMatchingPrincipal()
    {
        var valid = await UserSessionVersionValidator.ValidateAsync(
            _services, Principal(_user, sessionVersion: 0));

        Assert.True(valid);
    }

    [Fact]
    public async Task DisabledAccount_RejectsPreviouslyIssuedPrincipal()
    {
        _user.Disable();
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var valid = await UserSessionVersionValidator.ValidateAsync(
            _services, Principal(_user, sessionVersion: 0), TestContext.Current.CancellationToken);

        Assert.False(valid);
    }

    [Fact]
    public async Task SessionVersionMismatch_RejectsOldPrincipal()
    {
        _user.BumpSessionVersion();
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var valid = await UserSessionVersionValidator.ValidateAsync(
            _services, Principal(_user, sessionVersion: 0), TestContext.Current.CancellationToken);

        Assert.False(valid);
    }

    [Fact]
    public async Task NewPrincipalAfterSessionBump_RefreshesCacheAndRemainsValid()
    {
        Assert.True(await UserSessionVersionValidator.ValidateAsync(
            _services, Principal(_user, sessionVersion: 0), TestContext.Current.CancellationToken));

        _user.BumpSessionVersion();
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var newPrincipal = Principal(_user, sessionVersion: _user.SessionVersion);
        Assert.True(await UserSessionVersionValidator.ValidateAsync(
            _services, newPrincipal, TestContext.Current.CancellationToken));

        Assert.False(await UserSessionVersionValidator.ValidateAsync(
            _services, Principal(_user, sessionVersion: 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdminClaimMustMatchPersistedAccountState()
    {
        _user.SetAdmin(true);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stalePrincipal = Principal(_user, sessionVersion: _user.SessionVersion, isAdmin: false);
        var currentPrincipal = Principal(_user, sessionVersion: _user.SessionVersion, isAdmin: true);

        Assert.False(await UserSessionVersionValidator.ValidateAsync(
            _services, stalePrincipal, TestContext.Current.CancellationToken));
        Assert.True(await UserSessionVersionValidator.ValidateAsync(
            _services, currentPrincipal, TestContext.Current.CancellationToken));
    }

    private static ClaimsPrincipal Principal(User user, int sessionVersion, bool? isAdmin = null)
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim("sv", sessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim("isAdmin", (isAdmin ?? user.IsAdmin).ToString()));
        return new ClaimsPrincipal(identity);
    }
}
