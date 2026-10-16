namespace ProjectBeacon.Application.Tests;

using Application.Security;
using Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Builds real AesSecretProtector instances for handler tests.
/// </summary>
internal static class TestProtectors
{
    public const string CurrentKey = "test-current-data-key-material";
    public const string LegacyKey = "test-legacy-data-key-material";
    private const string ProdCurrentKey = "test-current-data-key-material-at-least-32-chars";

    /// <summary>Safe-mode (Development) protector: short keys allowed, write-v2 on by default.</summary>
    public static AesSecretProtector Dev(string? legacy = null, bool writeV2 = true)
        => Create(CurrentKey, legacy, writeV2, env: "Development");

    /// <summary>
    /// Non-safe (Production) protector: the legacy material is optional (absent when
    /// <paramref name="legacy"/> is null).
    /// </summary>
    public static AesSecretProtector Prod(string? legacy = null, bool writeV2 = true)
        => Create(ProdCurrentKey, legacy, writeV2, env: "Production");

    public static AesSecretProtector Create(
        string current,
        string? legacy = null,
        bool writeV2 = true,
        string env = "Development")
    {
        var values = new Dictionary<string, string?>
        {
            [DataKeyPolicy.ConfigKey] = current,
            [DataKeyPolicy.WriteV2Key] = writeV2 ? "true" : "false"
        };
        if (legacy is not null)
            values[DataKeyPolicy.LegacyConfigKey] = legacy;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AesSecretProtector(config, new TestHostEnvironment(env), NullLogger<AesSecretProtector>.Instance);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "BeaconTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = environmentName;
    }
}
