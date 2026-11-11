namespace ProjectBeacon.Application.Tests;

using Application.Auth;
using Application.Common;
using Application.Persistence;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public sealed class BootstrapHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private const string BootstrapToken = "bootstrap-test-secret";

    public BootstrapHandlerTests()
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
    public async Task Bootstrap_MissingConfiguredToken_FailsClosed()
    {
        var handler = new BootstrapHandler(
            _factory,
            new BcryptPasswordHasher(),
            Configuration(token: null));

        var result = await handler.HandleAsync(BootstrapToken, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Unauthorized, result.Kind);
        Assert.Empty(await _db.Users.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bootstrap_WrongToken_FailsClosed()
    {
        var handler = CreateHandler();

        var result = await handler.HandleAsync("wrong-token", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Unauthorized, result.Kind);
        Assert.Empty(await _db.Users.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bootstrap_SecondCall_ReturnsConflictWithoutCreatingAnotherAdmin()
    {
        var handler = CreateHandler();

        var first = await handler.HandleAsync(BootstrapToken, TestContext.Current.CancellationToken);
        var second = await handler.HandleAsync(BootstrapToken, TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.False(second.Success);
        Assert.Equal(ErrorKind.Conflict, second.Kind);
        Assert.Equal(1, await _db.Users.IgnoreQueryFilters()
            .CountAsync(user => user.IsAdmin, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bootstrap_ParallelRequests_CreateExactlyOneAdmin()
    {
        var firstHandler = CreateHandler();
        var secondHandler = CreateHandler();

        var results = await Task.WhenAll(
            firstHandler.HandleAsync(BootstrapToken, TestContext.Current.CancellationToken),
            secondHandler.HandleAsync(BootstrapToken, TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.Success);
        var rejected = Assert.Single(results, result => !result.Success);
        Assert.Equal(ErrorKind.Conflict, rejected.Kind);
        Assert.Equal(1, await _db.Users.IgnoreQueryFilters()
            .CountAsync(user => user.IsAdmin, TestContext.Current.CancellationToken));
    }

    private BootstrapHandler CreateHandler() =>
        new(_factory, new BcryptPasswordHasher(), Configuration());

    private static IConfiguration Configuration(string? token = BootstrapToken)
    {
        var values = new Dictionary<string, string?>();
        if (token is not null)
            values["BOOTSTRAP_ADMIN_TOKEN"] = token;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
