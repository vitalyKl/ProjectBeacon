namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF mapping for the Data Protection key ring. The row is not tenant-scoped and
/// carries no RLS policy, so the app role reads and writes it without a tenant GUC.
/// </summary>
public class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> entity)
    {
        entity.HasKey(e => e.KeyId);
        entity.Property(e => e.KeyId).HasMaxLength(128);
        entity.Property(e => e.XmlData).IsRequired();
    }
}
