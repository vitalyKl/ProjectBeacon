namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.CompletedAt);
        entity.Property(e => e.PipelineStage).HasConversion<string>();

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
    }
}
