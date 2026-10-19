namespace ProjectBeacon.Domain.Entities.Identity;

/// <summary>
/// Data Protection key ring row. The framework key id (a GUID rendered as a string)
/// is the natural key, so this row has no surrogate Entity.Id, like DecisionTask.
/// </summary>
public sealed class DataProtectionKey
{
    public DataProtectionKey() { }

    public DataProtectionKey(string keyId, string xmlData)
    {
        KeyId = keyId;
        XmlData = xmlData;
    }

    public string KeyId { get; set; } = string.Empty;
    public string XmlData { get; set; } = string.Empty;
}
