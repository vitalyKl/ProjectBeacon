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

    [Fact]
    public void ResetPassword_ClearsLockout()
    {
        var user = User.Create("test", "test@test.com", "hashed");
        user.RecordFailedLogin();
        user.RecordFailedLogin();
        user.RecordFailedLogin();
        user.RecordFailedLogin();
        user.RecordFailedLogin();
        Assert.True(user.IsLockedOut);

        user.ResetPassword("new-hash");

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
        Assert.False(user.IsLockedOut);
        Assert.Equal(1, user.SessionVersion);
    }

    [Fact]
    public void DisableAndEnable_AreIdempotentAndRevokeSessions()
    {
        var user = User.Create("test", "test@test.com", "hashed");
        var disabledAt = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

        user.Disable(disabledAt);

        Assert.True(user.IsDisabled);
        Assert.Equal(disabledAt, user.DisabledAt);
        Assert.Equal(1, user.SessionVersion);

        user.Disable(disabledAt.AddMinutes(1));
        Assert.Equal(1, user.SessionVersion);
        Assert.Equal(disabledAt, user.DisabledAt);

        user.Enable();

        Assert.False(user.IsDisabled);
        Assert.Null(user.DisabledAt);
        Assert.Equal(2, user.SessionVersion);

        user.Enable();
        Assert.Equal(2, user.SessionVersion);
    }

    [Fact]
    public void SetAdmin_BumpsSessionVersionOnlyWhenValueChanges()
    {
        var user = User.Create("test", "test@test.com", "hashed");

        user.SetAdmin(true);
        Assert.True(user.IsAdmin);
        Assert.Equal(1, user.SessionVersion);

        user.SetAdmin(true);
        Assert.Equal(1, user.SessionVersion);

        user.SetAdmin(false);
        Assert.False(user.IsAdmin);
        Assert.Equal(2, user.SessionVersion);
    }
}

public sealed class PasswordResetTokenTests
{
    [Fact]
    public void TryConsume_Succeeds_WhenFresh()
    {
        var token = PasswordResetToken.Create(Guid.CreateVersion7(), "hash");
        Assert.True(token.TryConsume());
        Assert.NotNull(token.UsedAt);
        Assert.False(token.TryConsume());
    }
}

public sealed class UserSessionTests
{
    [Fact]
    public void Deactivate_ClearsActive()
    {
        var session = UserSession.Create(Guid.CreateVersion7(), "127.0.0.1");
        Assert.True(session.IsActive);
        session.Deactivate();
        Assert.False(session.IsActive);
    }
}
