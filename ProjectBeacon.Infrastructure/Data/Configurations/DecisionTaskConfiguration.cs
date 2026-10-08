namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for the decision-to-task link.
/// </summary>
public class DecisionTaskConfiguration : IEntityTypeConfiguration<DecisionTask>
{
    public void Configure(EntityTypeBuilder<DecisionTask> entity)
    {
        entity.HasKey(e => new { e.DecisionId, e.TaskId });
        entity.HasOne(e => e.Decision)
            .WithMany(e => e.RelatedTasks)
            .HasForeignKey(e => e.DecisionId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
