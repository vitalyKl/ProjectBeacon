namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SubtaskConfiguration : IEntityTypeConfiguration<Subtask>
{
    public void Configure(EntityTypeBuilder<Subtask> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Instructions).IsRequired().HasMaxLength(5000);
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.DiffRef).HasMaxLength(500);
        entity.Property(e => e.Summary).HasMaxLength(5000);
        entity.Ignore(e => e.AllowedMcpTools);
        entity.Ignore(e => e.AllowedPaths);
        entity.Property(e => e.AllowedMcpToolsJson).IsRequired();
        entity.Property(e => e.AllowedPathsJson).IsRequired();
        entity.HasIndex(e => e.TaskId);
        entity.HasOne(e => e.Task)
            .WithMany(e => e.Subtasks)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
