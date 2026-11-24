namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Roadmap;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>EF mapping for project Releases.</summary>
public sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> entity)
    {
        entity.HasKey(release => release.Id);
        entity.Property(release => release.Name).IsRequired().HasMaxLength(200);
        entity.Property(release => release.Description).HasMaxLength(5000);
        entity.Property(release => release.TargetVersion).HasMaxLength(100);
        entity.Property(release => release.TargetBranch).IsRequired().HasMaxLength(200);
        entity.Property(release => release.Status).HasConversion<string>().IsRequired();
        entity.Property(release => release.Order).IsRequired();
        entity.Property(release => release.Revision)
            .HasDefaultValue(1)
            .IsRequired()
            .IsConcurrencyToken();
        entity.Property(release => release.CreatedAt).IsRequired();
        entity.Property(release => release.UpdatedAt);
        entity.Property(release => release.PublishedAt);

        entity.HasOne(release => release.Project)
            .WithMany(project => project.Releases)
            .HasForeignKey(release => release.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(release => new { release.ProjectId, release.Status, release.Order });
    }
}
