namespace ProjectBeacon.API.Tests;

using Application.Auth;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Domain.Entities.Identity;

public sealed class AuthIntegrationTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;

    public AuthIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;

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

    private string SeedBootstrap()
    {
        var handler = new BootstrapHandler(_db, null!);
        var result = handler.HandleAsync(string.Empty).Result;

        return result.Value.Password;
    }

    [Fact]
    public void Bootstrap_CreatesAdminUser()
    {
        var handler = new BootstrapHandler(_db, null!);
        var result = handler.HandleAsync(string.Empty).Result;

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.Value.UserId);
        Assert.Equal("admin", result.Value.Login);
        Assert.Equal("admin@beacon.local", result.Value.Email);
        Assert.True(result.Value.IsAdmin);
        Assert.NotEmpty(result.Value.Password);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal("admin", user.Login);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public void Bootstrap_AlreadyCompleted_ReturnsFailure()
    {
        var handler = new BootstrapHandler(_db, null!);
        handler.HandleAsync(string.Empty).Wait();

        var result = handler.HandleAsync(string.Empty).Result;

        Assert.False(result.Success);
        Assert.Equal("Bootstrap already completed.", result.Error);
    }

    [Fact]
    public void Login_WithWrongPassword_ReturnsFailure()
    {
        var password = SeedBootstrap();

        var handler = new LoginHandler(_db);
        var result = handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", "wrong-password"))).Result;

        Assert.False(result.Success);
        Assert.Equal("Invalid credentials.", result.Error);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal(1, user.FailedLoginAttempts);
    }

    [Fact]
    public void Login_WithCorrectPassword_Succeeds()
    {
        var password = SeedBootstrap();

        var handler = new LoginHandler(_db);
        var result = handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", password))).Result;

        Assert.True(result.Success);
        Assert.Equal("admin", result.Value.Login);
        Assert.True(result.Value.IsAdmin);

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public void Login_FiveFailedAttempts_LocksAccount()
    {
        var correctPassword = SeedBootstrap();

        var handler = new LoginHandler(_db);

        for (var i = 0; i < 5; i++)
        {
            handler.HandleAsync(
                new LoginCommand(new LoginRequest("admin", "wrong"))).Wait();
        }

        var user = _db.Users.FirstOrDefault();
        Assert.NotNull(user);
        Assert.True(user.IsLockedOut);
        Assert.NotNull(user.LockedUntil);

        var lockedResult = handler.HandleAsync(
            new LoginCommand(new LoginRequest("admin", correctPassword))).Result;

        Assert.False(lockedResult.Success);
        Assert.Equal("Account is temporarily locked. Try again later.", lockedResult.Error);
    }

    [Fact]
    public void Register_CreatesUser()
    {
        var handler = new RegisterHandler(_db);
        var result = handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "new@test.com", "password"))).Result;

        Assert.True(result.Success);
        Assert.Equal("newuser", result.Value.Login);
        Assert.Equal("new@test.com", result.Value.Email);

        var user = _db.Users.FirstOrDefault(u => u.Login == "newuser");
        Assert.NotNull(user);
    }

    [Fact]
    public void Register_DuplicateLogin_ReturnsFailure()
    {
        var handler = new RegisterHandler(_db);
        handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "new@test.com", "password"))).Wait();

        var result = handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("newuser", "other@test.com", "password"))).Result;

        Assert.False(result.Success);
        Assert.Equal("Login already taken.", result.Error);
    }

    [Fact]
    public void Register_DuplicateEmail_ReturnsFailure()
    {
        var handler = new RegisterHandler(_db);
        handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("user1", "test@test.com", "password"))).Wait();

        var result = handler.HandleAsync(
            new RegisterCommand(new RegisterRequest("user2", "test@test.com", "password"))).Result;

        Assert.False(result.Success);
        Assert.Equal("Email already registered.", result.Error);
    }
}
