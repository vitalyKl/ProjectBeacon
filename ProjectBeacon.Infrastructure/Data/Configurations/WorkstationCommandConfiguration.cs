namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Common;
using Domain.Entities.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for device commands. Indexed by device, status, and creation time.
/// </summary>
public class WorkstationCommandConfiguration : IEntityTypeConfiguration<WorkstationCommand>
{
    public void Configure(EntityTypeBuilder<WorkstationCommand> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Kind).HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.Version).HasDefaultValue(CommandProtocol.CurrentVersion).IsRequired();
        entity.Property(e => e.PayloadJson).IsRequired();
        entity.Property(e => e.Error).HasMaxLength(2000);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.Attempts).HasDefaultValue(0).IsRequired();
        entity.Property(e => e.ClaimInstanceId).HasMaxLength(64);
        entity.HasIndex(e => new { e.DeviceId, e.Status, e.CreatedAt });
        entity.HasOne<DaemonDevice>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
