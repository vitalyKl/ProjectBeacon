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
    public void ClientOptions_PrefersCanonicalApiEnvironmentValues_AndHonorsCommandLine()
    {
        var values = new Dictionary<string, string?>
        {
            ["BEACON_API_URL"] = "https://canonical.example.test",
            ["BEACON_API_TOKEN"] = "canonical-token",
            ["BEACON_URL"] = "http://legacy.example.test",
            ["BEACON_CLIENT_TOKEN"] = "legacy-token"
        };

        var fromEnvironment = ClientOptions.Parse([], name => values.GetValueOrDefault(name));
        Assert.Equal("https://canonical.example.test", fromEnvironment.Url);
        Assert.Equal("canonical-token", fromEnvironment.Token);

        var fromArguments = ClientOptions.Parse(
            ["--url", "https://argument.example.test", "--token", "argument-token"],
            name => values.GetValueOrDefault(name));
        Assert.Equal("https://argument.example.test", fromArguments.Url);
        Assert.Equal("argument-token", fromArguments.Token);
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

    [Fact]
    public void SubprocessEnvironmentPolicies_AreCentralizedAndKeepCredentialBoundary()
    {
        var mcpNames = ClientConfiguration.McpSubprocessEnvironmentNames;
        var nativeNames = ClientConfiguration.NativeCommandEnvironmentNames;

        Assert.Contains("PATH", mcpNames);
        Assert.Contains("HOME", mcpNames);
        Assert.DoesNotContain("JAVA_HOME", mcpNames);
        Assert.Contains("JAVA_HOME", nativeNames);
        Assert.Contains("RUSTUP_TOOLCHAIN", nativeNames);
        Assert.DoesNotContain("BEACON_API_TOKEN", mcpNames);
        Assert.DoesNotContain("BEACON_API_TOKEN", nativeNames);

        var captured = ClientConfiguration.CaptureAllowedEnvironment(
            new[] { "PATH", "JAVA_HOME", "BEACON_API_TOKEN", "MISSING" },
            name => name switch
            {
                "PATH" => "/usr/bin",
                "JAVA_HOME" => "/opt/java",
                "BEACON_API_TOKEN" => "must-not-be-inherited",
                _ => null
            });

        Assert.Equal(3, captured.Count);
        Assert.Equal("/usr/bin", captured["PATH"]);
        Assert.Equal("/opt/java", captured["JAVA_HOME"]);
        Assert.Equal("must-not-be-inherited", captured["BEACON_API_TOKEN"]);
    }

    [Fact]
    public void McpStartupDiagnostic_UsesOneModeAwareMessageAndDocumentsLegacyAliases()
    {
        Assert.Contains("BEACON_CLIENT_TOKEN", ClientConfiguration.McpStartupDiagnostic(
            misconfigured: true, configured: false));
        Assert.Contains("file tools remain available", ClientConfiguration.McpStartupDiagnostic(
            misconfigured: false, configured: false));
        Assert.Equal("beacon mcp: authenticated API mode.", ClientConfiguration.McpStartupDiagnostic(
            misconfigured: false, configured: true));
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
