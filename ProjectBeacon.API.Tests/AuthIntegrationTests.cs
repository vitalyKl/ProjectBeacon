namespace ProjectBeacon.API.Tests;

using Application.Auth;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Domain.Entities.Identity;

public sealed class AuthIntegrationTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;

    public AuthIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbFactory = new BeaconDbFactory(options, null);
        _db = new BeaconDbContext(options);
        _unscoped = TenantScope.EnterUnscoped();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private async Task<string> SeedBootstrapAsync()
    {
        var handler = new BootstrapHandler(_dbFactory, null!);
        var result = await handler.HandleAsync(string.Empty);

        return result.Value!.Password;
    }

    [Fact]
    public async Task Bootstrap_CreatesAdminUser()
    {
        var handler = new BootstrapHandler(_dbFactory, null!);
        var result = await handler.HandleAsync(string.Empty);

        Assert.True(result.Success);
        var admin = result.Value!;
        Assert.NotEqual(Guid.Empty, admin.UserId);
        Assert.Equal("admin", admin.Login);
        Assert.Equal("admin@beacon.local", admin.Email);
        Assert.True(admin.IsAdmin);
        Assert.NotEmpty(admin.Password);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal("admin", user.Login);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task Bootstrap_AlreadyCompleted_ReturnsFailure()
    {
        var handler = new BootstrapHandler(_dbFactory, null!);
        await handler.HandleAsync(string.Empty);

        var result = await handler.HandleAsync(string.Empty);

        Assert.False(result.Success);
        Assert.Equal("Bootstrap already completed.", result.Error);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsFailure()
    {
        await SeedBootstrapAsync();

        var handler = new LoginHandler(_dbFactory);
        var result = await handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", "wrong-password")));

        Assert.False(result.Success);
        Assert.Equal("Invalid credentials.", result.Error);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal(1, user.FailedLoginAttempts);
    }

    [Fact]
    public async Task Login_WithCorrectPassword_Succeeds()
    {
        var password = await SeedBootstrapAsync();

        var handler = new LoginHandler(_dbFactory);
        var result = await handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", password)));

        Assert.True(result.Success);
        var login = result.Value!;
        Assert.Equal("admin", login.Login);
        Assert.True(login.IsAdmin);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task Login_FiveFailedAttempts_LocksAccount()
    {
        var correctPassword = await SeedBootstrapAsync();

        var handler = new LoginHandler(_dbFactory);

        for (var i = 0; i < 5; i++)
        {
            await handler.HandleAsync(
                new LoginCommand(new LoginRequest("admin", "wrong")));
        }

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.True(user.IsLockedOut);
        Assert.NotNull(user.LockedUntil);

        var lockedResult = await handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", correctPassword)));

        Assert.False(lockedResult.Success);
        Assert.Equal("Account is temporarily locked. Try again later.", lockedResult.Error);
    }

    [Fact]
    public async Task Register_CreatesUser()
    {
        var handler = new RegisterHandler(_dbFactory);
        var result = await handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "new@test.com", "password")));

        Assert.True(result.Success);
        var registered = result.Value!;
        Assert.Equal("newuser", registered.Login);
        Assert.Equal("new@test.com", registered.Email);

        var user = _db.Users.FirstOrDefault(u => u.Login == "newuser");
        Assert.NotNull(user);
    }

    [Fact]
    public async Task Register_DuplicateLogin_ReturnsFailure()
    {
        var handler = new RegisterHandler(_dbFactory);
        await handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "new@test.com", "password")));

        var result = await handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "other@test.com", "password")));

        Assert.False(result.Success);
        Assert.Equal("Login already taken.", result.Error);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsFailure()
    {
        var handler = new RegisterHandler(_dbFactory);
        await handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("user1", "test@test.com", "password")));

        var result = await handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("user2", "test@test.com", "password")));

        Assert.False(result.Success);
        Assert.Equal("Email already registered.", result.Error);
    }
}
