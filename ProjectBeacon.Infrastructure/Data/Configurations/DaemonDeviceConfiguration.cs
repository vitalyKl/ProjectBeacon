namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class DaemonDeviceConfiguration : IEntityTypeConfiguration<DaemonDevice>
{
    public void Configure(EntityTypeBuilder<DaemonDevice> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Fingerprint).IsRequired().HasMaxLength(200);
        entity.Property(e => e.TokenHash).IsRequired();
        entity.Property(e => e.TokenPrefix).IsRequired().HasMaxLength(20);
        entity.Property(e => e.ProbeJson).IsRequired();
        entity.Property(e => e.WorkstationJson).IsRequired();
        entity.Property(e => e.DesiredWorkstationJson).IsRequired();
        entity.Property(e => e.DesiredRevision).IsRequired();
        entity.Property(e => e.AppliedRevision).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => new { e.UserId, e.Fingerprint });
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
