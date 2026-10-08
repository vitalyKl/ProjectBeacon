namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for task kinds. Indexed by user.
/// </summary>
public class TaskKindConfiguration : IEntityTypeConfiguration<TaskKind>
{
    public void Configure(EntityTypeBuilder<TaskKind> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.IsBuiltIn).IsRequired();
        entity.HasIndex(e => e.UserId);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.DecisionBackendId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.WorkerBackendId).OnDelete(DeleteBehavior.SetNull);
    }
}
