namespace ProjectBeacon.Application.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Security;

public sealed class OperatorRoleLoginTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly BcryptPasswordHasher _passwords = new();
    private readonly User _support;

    public OperatorRoleLoginTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);
        _support = User.Create("support-operator", "support@example.test", _passwords.Hash("valid-password"));
        _support.AssignOperatorRole("Support");
        _seedDb.Users.Add(_support);
        _seedDb.SaveChanges();
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SupportOperator_CanSignInToControlPlaneWithoutCustomerAdminBypass()
    {
        var handler = new LoginHandler(
            _factory,
            _passwords,
            tokens: null!,
            configuration: new ConfigurationBuilder().Build());

        var controlLogin = await handler.HandleOperatorAsync(
            new LoginCommand(new LoginRequest("support-operator", "valid-password", null)),
            TestContext.Current.CancellationToken);
        var customerLogin = await handler.HandleAsync(
            new LoginCommand(new LoginRequest("support-operator", "valid-password", null)),
            TestContext.Current.CancellationToken);

        Assert.True(controlLogin.Success, controlLogin.Error);
        Assert.True(controlLogin.Value!.IsAdmin, "Control-cookie login represents a verified operator principal.");
        Assert.True(customerLogin.Success, customerLogin.Error);
        Assert.False(customerLogin.Value!.IsAdmin, "Customer login must not get the administrator bypass.");
        Assert.False(_support.IsAdmin);
        Assert.Equal("Support", _support.OperatorRole);
    }
}
