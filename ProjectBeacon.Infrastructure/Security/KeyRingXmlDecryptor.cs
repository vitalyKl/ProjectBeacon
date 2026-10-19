namespace ProjectBeacon.Infrastructure.Security;

using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Opens sealed key-ring elements when the data protection runtime loads a key. The
/// runtime instantiates this type via its assembly-qualified name, so the primary
/// constructor resolves key material from the same configuration the wiring used;
/// the parameterless constructor is the last resort (process environment, then the
/// dev fallback key). Current key first, legacy key second, otherwise fail-closed.
/// </summary>
public sealed class KeyRingXmlDecryptor : IXmlDecryptor
{
    private readonly string _currentKey;
    private readonly string? _legacyKey;

    public KeyRingXmlDecryptor(IServiceProvider provider)
    {
        var config = provider.GetRequiredService<IConfiguration>();
        var environment = provider.GetService<IHostEnvironment>()?.EnvironmentName
            ?? config["ASPNETCORE:Environment"]
            ?? config["ASPNETCORE_ENVIRONMENT"]
            ?? "Production";
        var keys = DataKeyPolicy.ResolveDataKeys(config, environment);
        _currentKey = keys.Current;
        _legacyKey = keys.Legacy;
    }

    public KeyRingXmlDecryptor()
        : this(
            Environment.GetEnvironmentVariable(DataKeyPolicy.ConfigKey) ?? DataKeyPolicy.DevFallbackKey,
            Environment.GetEnvironmentVariable(DataKeyPolicy.LegacyConfigKey))
    {
    }

    private KeyRingXmlDecryptor(string currentKey, string? legacyKey)
    {
        _currentKey = currentKey;
        _legacyKey = legacyKey;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var blob = encryptedElement.Value;
        string plaintext;
        try
        {
            plaintext = KeyRingSeal.Open(blob, _currentKey);
        }
        catch (CryptographicException)
        {
            if (_legacyKey is null)
                throw;
            plaintext = KeyRingSeal.Open(blob, _legacyKey);
        }
        return XElement.Parse(plaintext);
    }
}
