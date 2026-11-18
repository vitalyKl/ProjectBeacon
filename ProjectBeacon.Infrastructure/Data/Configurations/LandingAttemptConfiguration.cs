namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Tasks;

public sealed class LandingAttemptConfiguration : IEntityTypeConfiguration<LandingAttempt>
{
    public void Configure(EntityTypeBuilder<LandingAttempt> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Status).HasConversion<string>().IsRequired().HasMaxLength(24);
        entity.Property(e => e.Rationale).IsRequired().HasMaxLength(1000);
        entity.Property(e => e.SourceBranch).IsRequired().HasMaxLength(200);
        entity.Property(e => e.SourceSha).IsRequired().HasMaxLength(64);
        entity.Property(e => e.DeviceId).IsRequired();
        entity.Property(e => e.ReviewTreeSha).IsRequired().HasMaxLength(64);
        entity.Property(e => e.ReviewCommandHash).IsRequired().HasMaxLength(64);
        entity.Property(e => e.TargetBranch).IsRequired().HasMaxLength(200);
        entity.Property(e => e.TargetHeadSha).HasMaxLength(64);
        entity.Property(e => e.CandidateTreeSha).HasMaxLength(64);
        entity.Property(e => e.CandidateCheckOutputHash).HasMaxLength(64);
        entity.Property(e => e.ConflictFilesJson).HasColumnType("jsonb");
        entity.Property(e => e.FailureCode).HasMaxLength(80);
        entity.Property(e => e.EscalationCode).HasMaxLength(80);
        entity.Property(e => e.GitPolicyRevision).IsRequired().HasDefaultValue(1);
        entity.Property(e => e.GitPolicySnapshotJson).IsRequired().HasColumnType("jsonb").HasDefaultValue("{}");
        entity.Property(e => e.MergeCommitSha).HasMaxLength(64);
        entity.Property(e => e.RevertCommitSha).HasMaxLength(64);
        entity.Property(e => e.PostMergeCheckExitCode);
        entity.Property(e => e.PostMergeCheckOutputHash).HasMaxLength(64);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.TaskId, e.CreatedAt });
        entity.HasIndex(e => new { e.ProjectId, e.Status, e.CreatedAt });
        entity.HasIndex(e => new { e.ProjectId, e.TaskId })
            .IsUnique()
            .HasDatabaseName("IX_LandingAttempts_ProjectId_TaskId_Active")
            .HasFilter("\"Status\" IN ('PreviewPending', 'Previewed', 'ExecutePending', 'Landed', 'LandedUnverified', 'RevertPending', 'RevertedUnverified', 'RevertFailed')");
        entity.HasOne<ProjectBeacon.Domain.Entities.Projects.Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
