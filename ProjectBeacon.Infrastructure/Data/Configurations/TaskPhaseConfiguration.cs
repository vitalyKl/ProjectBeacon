namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TaskPhaseConfiguration : IEntityTypeConfiguration<TaskPhase>
{
    public void Configure(EntityTypeBuilder<TaskPhase> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Key).HasMaxLength(40);
        entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Instruction).HasMaxLength(2000);
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.HasIndex(e => new { e.TaskId, e.SortOrder });
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.ModelBackendId).OnDelete(DeleteBehavior.SetNull);
    }
}
