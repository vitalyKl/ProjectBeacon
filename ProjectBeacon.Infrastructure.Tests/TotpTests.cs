namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Security;

public sealed class TotpTests
{
    [Fact]
    public void Rfc6238_Vector_At59Seconds()
    {
        var code = Totp.Hotp("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", 59 / 30);
        Assert.Equal("287082", code);
    }
}
