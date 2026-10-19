namespace ProjectBeacon.Infrastructure.Security;

using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

/// <summary>
/// Seals the descriptor payload of a Data Protection key element before it is stored
/// in the repository. The framework wraps the returned element in its own
/// encrypted-secret envelope and records <see cref="KeyRingXmlDecryptor"/> as the
/// decryptor type; the stored key element keeps its plaintext id attribute.
/// </summary>
public sealed class KeyRingXmlEncryptor : IXmlEncryptor
{
    private readonly string _keyMaterial;

    public KeyRingXmlEncryptor(string keyMaterial)
    {
        _keyMaterial = keyMaterial;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var blob = KeyRingSeal.Seal(plaintextElement.ToString(SaveOptions.DisableFormatting), _keyMaterial);
        return new EncryptedXmlInfo(new XElement("keyRingSeal", blob), typeof(KeyRingXmlDecryptor));
    }
}
