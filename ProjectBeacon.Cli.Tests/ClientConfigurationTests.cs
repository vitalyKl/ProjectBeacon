namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class ClientConfigurationTests
{
    [Fact]
    public void ResolveValue_UsesCommandLineThenEnvironmentThenPersistedValue()
    {
        Assert.Equal("argument", ClientConfiguration.ResolveValue(" argument ", "environment", "client.json"));
        Assert.Equal("environment", ClientConfiguration.ResolveValue(null, " environment ", "client.json"));
        Assert.Equal("client.json", ClientConfiguration.ResolveValue(" ", null, " client.json "));
        Assert.Null(ClientConfiguration.ResolveValue(null, " ", null));
    }

    [Fact]
    public void ResolveApi_AcceptsCanonicalNamesAndLegacyWorkstationAliases()
    {
        var canonical = ClientConfiguration.ResolveApi(name => name switch
        {
            "BEACON_API_URL" => "https://api.example.test",
            "BEACON_API_TOKEN" => "secret",
            _ => null
        });
        Assert.True(canonical.Configured);
        Assert.False(canonical.Misconfigured);
        Assert.Equal(new Uri("https://api.example.test/"), canonical.BaseUri);
        Assert.Equal("secret", canonical.Token);

        var legacy = ClientConfiguration.ResolveApi(name => name switch
        {
            "BEACON_URL" => "http://localhost:5000/",
            "BEACON_CLIENT_TOKEN" => "legacy-secret",
            _ => null
        });
        Assert.True(legacy.Configured);
        Assert.Equal(new Uri("http://localhost:5000/"), legacy.BaseUri);
        Assert.Equal("legacy-secret", legacy.Token);
    }

    [Fact]
    public void ResolveApi_RejectsPartialCredentialsAndNonHttpUrls()
    {
        var partial = ClientConfiguration.ResolveApi(name =>
            name == "BEACON_API_URL" ? "https://api.example.test" : null);
        Assert.True(partial.Misconfigured);
        Assert.False(partial.Configured);

        var invalid = ClientConfiguration.ResolveApi(name => name switch
        {
            "BEACON_API_URL" => "file:///tmp/beacon",
            "BEACON_API_TOKEN" => "secret",
            _ => null
        });
        Assert.True(invalid.Misconfigured);
        Assert.False(invalid.Configured);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void IsEnabled_ParsesSupportedBooleanValues(string? value, bool expected)
        => Assert.Equal(expected, ClientConfiguration.IsEnabled(value));
}
