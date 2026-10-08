namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for OpenCode connections. User and provider together are unique.
/// </summary>
public class OpenCodeConnectionConfiguration : IEntityTypeConfiguration<OpenCodeConnection>
{
    public void Configure(EntityTypeBuilder<OpenCodeConnection> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.ProviderId).IsRequired().HasMaxLength(80);
        entity.Property(e => e.ModelId).IsRequired().HasMaxLength(120);
        entity.Property(e => e.BaseUrl).HasMaxLength(300);
        entity.Property(e => e.ApiKeyCipher).HasMaxLength(2000);
        entity.HasIndex(e => new { e.UserId, e.ProviderId }).IsUnique();
    }
}
