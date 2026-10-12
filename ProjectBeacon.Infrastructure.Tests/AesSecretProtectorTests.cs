namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

public sealed class AesSecretProtectorTests
{
    [Fact]
    public void RoundTrip_ConfiguredKey_Works()
    {
        // Key is supplied only through configuration (no environment variable),
        // matching a setup where JWT:Secret lives in appsettings rather than the environment.
        var protector = Create(new string('k', 40), "Production");

        var packed = protector.Seal("hunter2", protector.KeyMaterial());

        Assert.Equal("hunter2", protector.Open(packed, protector.KeyMaterial()));
    }

    [Fact]
    public void KeyMaterial_ReturnsConfiguredValue()
    {
        var protector = Create("a-very-long-configured-key-value-here", "Production");

        Assert.Equal("a-very-long-configured-key-value-here", protector.KeyMaterial());
    }

    [Fact]
    public void OldCiphertext_RemainsReadable()
    {
        const string key = "legacy-key-material";
        var packed = SecretBox.Seal("legacy-secret", key);

        var protector = Create(key, "Development");

        Assert.Equal("legacy-secret", protector.Open(packed, protector.KeyMaterial()));
    }

    private static AesSecretProtector Create(string? key, string env)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null)
            values[DataKeyPolicy.ConfigKey] = key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AesSecretProtector(config, new TestHostEnvironment(env));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "BeaconTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = environmentName;
    }
}
