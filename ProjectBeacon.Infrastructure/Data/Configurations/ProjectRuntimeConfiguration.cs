namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for project runtimes. Project and device together are unique.
/// </summary>
public class ProjectRuntimeConfiguration : IEntityTypeConfiguration<ProjectRuntime>
{
    public void Configure(EntityTypeBuilder<ProjectRuntime> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.LocalRoot).IsRequired().HasMaxLength(1000);
        entity.Property(e => e.ConfigRevision).IsRequired();
        entity.Property(e => e.AppliedConfigRevision).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.DeviceId }).IsUnique();
        entity.HasOne<Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<DaemonDevice>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
