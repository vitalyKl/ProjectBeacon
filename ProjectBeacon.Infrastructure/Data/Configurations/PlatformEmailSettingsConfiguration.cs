namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Email;

public sealed class PlatformEmailSettingsConfiguration : IEntityTypeConfiguration<PlatformEmailSettings>
{
    public void Configure(EntityTypeBuilder<PlatformEmailSettings> entity)
    {
        entity.HasKey(e=>e.Id);
        entity.Property(e=>e.Id).ValueGeneratedNever();
        entity.Property(e=>e.Host).IsRequired().HasMaxLength(255);
        entity.Property(e=>e.Port).IsRequired();
        entity.Property(e=>e.Username).IsRequired().HasMaxLength(320);
        entity.Property(e=>e.PasswordCipher).IsRequired().HasMaxLength(4096);
        entity.Property(e=>e.FromAddress).IsRequired().HasMaxLength(320);
        entity.Property(e=>e.Enabled).IsRequired();
        entity.Property(e=>e.UseStartTls).IsRequired();
        entity.Property(e=>e.UpdatedAt).IsRequired();
    }
}
