namespace ProjectBeacon.Infrastructure.Data;

using System.Xml.Linq;
using Domain.Entities.Identity;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Data Protection key ring over the DataProtectionKeys table. Creates a fresh
/// tenant-less context per call; the table has no RLS policy, so no tenant GUC is set.
/// </summary>
public sealed class DataProtectionKeyRepository : IXmlRepository
{
    private readonly DbContextOptions<BeaconDbContext> _options;

    public DataProtectionKeyRepository(DbContextOptions<BeaconDbContext> options)
    {
        _options = options;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = new BeaconDbContext(_options);
        return db.DataProtectionKeys
            .Select(e => e.XmlData)
            .ToList()
            .Select(XElement.Parse)
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = new BeaconDbContext(_options);
        var keyId = KeyIdOf(element, friendlyName);
        var xml = element.ToString(SaveOptions.DisableFormatting);
        var existing = db.DataProtectionKeys.Find(keyId);
        if (existing is null)
            db.DataProtectionKeys.Add(new DataProtectionKey(keyId, xml));
        else
            existing.XmlData = xml;
        db.SaveChanges();
    }

    private static string KeyIdOf(XElement element, string friendlyName)
    {
        var id = element.Attribute("id")?.Value ?? "*";
        // Revocation rows carry the store name so repeated revocations stay distinct.
        return element.Name.LocalName == "revocation" ? $"revocation:{id}#{friendlyName}" : id;
    }
}
