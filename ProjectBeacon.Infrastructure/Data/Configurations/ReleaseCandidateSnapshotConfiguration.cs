namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Roadmap;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>EF mapping for immutable release candidate evidence.</summary>
public sealed class ReleaseCandidateSnapshotConfiguration : IEntityTypeConfiguration<ReleaseCandidateSnapshot>
{
    public void Configure(EntityTypeBuilder<ReleaseCandidateSnapshot> entity)
    {
        entity.HasKey(snapshot => snapshot.Id);
        entity.HasAlternateKey(snapshot => new { snapshot.ProjectId, snapshot.ReleaseId });

        entity.Property(snapshot => snapshot.Version).IsRequired().HasMaxLength(100);
        entity.Property(snapshot => snapshot.TagName).IsRequired().HasMaxLength(200);
        entity.Property(snapshot => snapshot.TargetBranch).IsRequired().HasMaxLength(200);
        entity.Property(snapshot => snapshot.BaseSha).IsRequired().HasMaxLength(64);
        entity.Property(snapshot => snapshot.PreparedCommitSha).IsRequired().HasMaxLength(64);
        entity.Property(snapshot => snapshot.CandidateSha).IsRequired().HasMaxLength(64);
        entity.Property(snapshot => snapshot.CandidateTreeSha).IsRequired().HasMaxLength(64);
        entity.Property(snapshot => snapshot.VersionPolicyRevision).IsRequired();
        entity.Property(snapshot => snapshot.ReleaseRevision).IsRequired();
        entity.Property(snapshot => snapshot.ManifestSha256).IsRequired().HasMaxLength(64);
        entity.Property(snapshot => snapshot.SnapshotJson).IsRequired().HasColumnType("jsonb");
        entity.Property(snapshot => snapshot.PreparedAtUtc).IsRequired();

        entity.HasOne(snapshot => snapshot.Project)
            .WithMany()
            .HasForeignKey(snapshot => snapshot.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(snapshot => new { snapshot.ProjectId, snapshot.ReleaseId })
            .IsUnique();
        entity.HasIndex(snapshot => new { snapshot.ProjectId, snapshot.CandidateSha });
    }
}
