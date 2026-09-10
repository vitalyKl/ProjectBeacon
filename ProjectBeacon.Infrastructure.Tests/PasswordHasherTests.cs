namespace ProjectBeacon.Infrastructure.Tests;

using ProjectBeacon.Infrastructure.Security;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_ReturnsNonEmptyString()
    {
        var hash = PasswordHasher.Hash("password123");

        Assert.NotEmpty(hash);
    }

    [Fact]
    public void Verify_AcceptsCorrectPassword()
    {
        var hash = PasswordHasher.Hash("mypassword");
        var result = PasswordHasher.Verify("mypassword", hash);

        Assert.True(result);
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hash = PasswordHasher.Hash("mypassword");
        var result = PasswordHasher.Verify("wrongpassword", hash);

        Assert.False(result);
    }

    [Fact]
    public void GenerateRandomPassword_ReturnsCorrectLength()
    {
        var password = PasswordHasher.GenerateRandomPassword(16);

        Assert.Equal(16, password.Length);
    }

    [Fact]
    public void GenerateRandomPassword_DefaultLengthIs32()
    {
        var password = PasswordHasher.GenerateRandomPassword();

        Assert.Equal(32, password.Length);
    }

    [Fact]
    public void GenerateRandomPassword_ProducesDifferentPasswords()
    {
        var pw1 = PasswordHasher.GenerateRandomPassword(16);
        var pw2 = PasswordHasher.GenerateRandomPassword(16);

        Assert.NotEqual(pw1, pw2);
    }
}
