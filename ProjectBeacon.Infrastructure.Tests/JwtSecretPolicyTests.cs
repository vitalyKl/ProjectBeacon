namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Security;

public sealed class JwtSecretPolicyTests
{
    [Fact]
    public void EnsureConfigured_Production_MissingSecret_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSecretPolicy.EnsureConfigured(null, "Production"));

        Assert.Contains("JWT:Secret", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void EnsureConfigured_Production_EmptySecret_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSecretPolicy.EnsureConfigured("", "Production"));

        Assert.Contains("JWT:Secret", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void EnsureConfigured_Production_ShortSecret_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSecretPolicy.EnsureConfigured(new string('a', 31), "Production"));

        Assert.Contains("31", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void EnsureConfigured_Production_ExactMinimumSecret_ReturnsIt()
    {
        var secret = new string('a', 32);

        Assert.Equal(secret, JwtSecretPolicy.EnsureConfigured(secret, "Production"));
    }

    [Fact]
    public void EnsureConfigured_Production_LongSecret_ReturnsIt()
    {
        var secret = "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long";

        Assert.Equal(secret, JwtSecretPolicy.EnsureConfigured(secret, "Production"));
    }

    [Fact]
    public void EnsureConfigured_Development_NullSecret_StillThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSecretPolicy.EnsureConfigured(null, "Development"));

        Assert.Contains("JWT secret key not configured.", ex.Message);
    }

    [Fact]
    public void EnsureConfigured_Development_EmptySecret_Allowed()
    {
        Assert.Equal("", JwtSecretPolicy.EnsureConfigured("", "Development"));
    }
}
