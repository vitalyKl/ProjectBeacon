namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Evals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for review runs. Indexed by project and task.
/// </summary>
public class ReviewRunConfiguration : IEntityTypeConfiguration<ReviewRun>
{
    public void Configure(EntityTypeBuilder<ReviewRun> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.ReviewerType).HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.Findings);
        entity.Property(e => e.ArtifactRef).HasMaxLength(500);
        entity.Property(e => e.TranscriptRef).HasMaxLength(500).IsRequired();
        entity.Property(e => e.ReviewerActorId).HasMaxLength(100);
        entity.Property(e => e.StartedAt).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.TaskId });
    }
}
