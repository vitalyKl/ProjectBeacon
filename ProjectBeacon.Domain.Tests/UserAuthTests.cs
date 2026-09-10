namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Identity;

public sealed class UserAuthTests
{
    [Fact]
    public void Create_SetsDefaultValues()
    {
        var user = User.Create("test", "test@test.com", "hashed");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("test", user.Login);
        Assert.Equal("test@test.com", user.Email);
        Assert.False(user.IsAdmin);
        Assert.Null(user.LastLoginAt);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void Create_Admin_SetsIsAdmin()
    {
        var user = User.Create("admin", "admin@test.com", "hashed", isAdmin: true);

        Assert.True(user.IsAdmin);
    }

    [Fact]
    public void RecordLogin_ClearsFailedAttempts()
    {
        var user = User.Create("test", "test@test.com", "hashed");
        user.RecordFailedLogin();
        user.RecordFailedLogin();

        Assert.Equal(2, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);

        user.RecordLogin();

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.NotNull(user.LastLoginAt);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void RecordFailedLogin_LocksAfterFiveAttempts()
    {
        var user = User.Create("test", "test@test.com", "hashed");

        user.RecordFailedLogin();
        user.RecordFailedLogin();
        user.RecordFailedLogin();
        user.RecordFailedLogin();

        Assert.Equal(4, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
        Assert.False(user.IsLockedOut);

        user.RecordFailedLogin();

        Assert.Equal(5, user.FailedLoginAttempts);
        Assert.NotNull(user.LockedUntil);
        Assert.True(user.IsLockedOut);
    }

    [Fact]
    public void IsLockedOut_ReturnsFalse_WhenNotLocked()
    {
        var user = User.Create("test", "test@test.com", "hashed");
        Assert.False(user.IsLockedOut);
    }
}
