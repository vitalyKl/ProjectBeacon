namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for task dependencies. The task pair is unique.
/// </summary>
public class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Task)
            .WithMany(e => e.Dependencies)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.DependentTask)
            .WithMany()
            .HasForeignKey(e => e.DependentTaskId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => new { e.TaskId, e.DependentTaskId }).IsUnique();
    }
}
