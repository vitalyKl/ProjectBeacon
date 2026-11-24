namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for tasks. Indexed by project and status.
/// </summary>
public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
        entity.Property(e => e.Description).HasMaxLength(5000);
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.Priority).HasConversion<string>().IsRequired();
        entity.Property(e => e.Type).HasConversion<string>().IsRequired();
        entity.Property(e => e.SubStage).HasConversion<string>();
        entity.Property(e => e.ReviewNotes).HasMaxLength(2000);
        entity.Property(e => e.VersionImpact).HasMaxLength(16);
        entity.Property(e => e.VersionImpactRationale).HasMaxLength(1000);
        entity.Property(e => e.VersionImpactAt);
        entity.Property(e => e.VersionImpactReviewStatus).HasMaxLength(16);
        entity.Property(e => e.VersionImpactReviewNote).HasMaxLength(1000);
        entity.Property(e => e.VersionImpactReviewedAt);
        entity.Property(e => e.VersionImpactReviewedBySessionId);
        entity.Property(e => e.VersionImpactRevision).HasDefaultValue(1).IsRequired().IsConcurrencyToken();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.CompletedAt);
        entity.Property(e => e.PipelineStage).HasConversion<string>();
        entity.Property(e => e.PipelineDeviceId);
        entity.Property(e => e.HotfixSourceReleaseId);
        entity.Property(e => e.HotfixDeployedSha).HasMaxLength(64);
        entity.Property(e => e.HotfixTargetBranch).HasMaxLength(200);
        entity.Property(e => e.HotfixRequestId);
        entity.Property(e => e.ForwardPortOfTaskId);
        entity.Property(e => e.StateRevision).HasDefaultValue(0).IsRequired();
        entity.Property(e => e.ContentRevision).HasDefaultValue(1).IsRequired().IsConcurrencyToken();

        entity.HasOne(e => e.Project)
            .WithMany(e => e.Tasks)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Label)
            .WithMany(e => e.Tasks)
            .HasForeignKey(e => e.LabelId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.Milestone)
            .WithMany(e => e.Tasks)
            .HasForeignKey(e => e.MilestoneId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne<Release>()
            .WithMany()
            .HasForeignKey(e => new { e.ProjectId, e.HotfixSourceReleaseId })
            .HasPrincipalKey(release => new { release.ProjectId, release.Id })
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(e => new { e.ProjectId, e.ForwardPortOfTaskId })
            .HasPrincipalKey(task => new { task.ProjectId, task.Id })
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasAlternateKey(e => new { e.ProjectId, e.Id });

        entity.HasMany(e => e.Comments)
            .WithOne(e => e.Task)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Dependencies)
            .WithOne(e => e.Task)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany<TaskStep>()
            .WithOne()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.ProjectId, e.Status });
        entity.HasIndex(e => new { e.ProjectId, e.HotfixRequestId })
            .IsUnique()
            .HasFilter("\\"HotfixRequestId\\" IS NOT NULL");
        entity.HasIndex(e => new { e.ProjectId, e.ForwardPortOfTaskId })
            .IsUnique()
            .HasFilter("\\"ForwardPortOfTaskId\\" IS NOT NULL");
    }
}
