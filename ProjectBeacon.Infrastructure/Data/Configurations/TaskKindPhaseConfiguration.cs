namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class TaskKindPhaseConfiguration : IEntityTypeConfiguration<TaskKindPhase>
{
    public void Configure(EntityTypeBuilder<TaskKindPhase> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Key).HasMaxLength(40);
        entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Instruction).HasMaxLength(2000);
        entity.HasIndex(e => new { e.TaskKindId, e.SortOrder });
        entity.HasOne<TaskKind>().WithMany().HasForeignKey(e => e.TaskKindId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.ModelBackendId).OnDelete(DeleteBehavior.SetNull);
    }
}
