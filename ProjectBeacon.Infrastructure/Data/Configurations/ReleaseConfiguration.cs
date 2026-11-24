namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Roadmap;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>EF mapping for project Releases.</summary>
public sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> entity)
    {
        entity.HasKey(release => release.Id);
        entity.HasAlternateKey(release => new { release.ProjectId, release.Id });
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
        entity.Property(release => release.PreparationRevision)
            .HasDefaultValue(1).IsRequired().IsConcurrencyToken();
        entity.Property(release => release.PreparationStatus)
            .HasConversion<string>().HasDefaultValue(ReleasePreparationStatus.NotStarted).IsRequired();
        entity.Property(release => release.PreparationDeviceId);
        entity.Property(release => release.CandidateId);
        entity.Property(release => release.PreparationCommandId);
        entity.Property(release => release.VerificationCommandId);
        entity.Property(release => release.PreparationPayloadJson).HasColumnType("jsonb");
        entity.Property(release => release.PreparationResultJson).HasColumnType("jsonb");
        entity.Property(release => release.PreparationError).HasMaxLength(4000);
        entity.Property(release => release.PreparationUpdatedAtUtc);

        entity.HasOne(release => release.Project)
            .WithMany(project => project.Releases)
            .HasForeignKey(release => release.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The snapshot is navigable from its dependent row. The aggregate keeps an
        // in-memory CandidateSnapshot reference during the transition, but it is not
        // an EF navigation: migration snapshots model Release as a shared dictionary type.
        entity.Ignore(release => release.CandidateSnapshot);

        entity.HasIndex(release => new { release.ProjectId, release.Status, release.Order });
    }
}
