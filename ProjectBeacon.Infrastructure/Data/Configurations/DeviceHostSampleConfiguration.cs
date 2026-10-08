namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class DeviceHostSampleConfiguration : IEntityTypeConfiguration<DeviceHostSample>
{
    public void Configure(EntityTypeBuilder<DeviceHostSample> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.GpuName).HasMaxLength(200);
        entity.Property(e => e.SampledAt).IsRequired();
        entity.HasIndex(e => new { e.DeviceId, e.SampledAt });
        entity.HasOne<DaemonDevice>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
