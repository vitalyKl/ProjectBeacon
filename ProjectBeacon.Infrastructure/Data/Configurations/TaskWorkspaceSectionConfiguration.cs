namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskWorkspaceSectionConfiguration : IEntityTypeConfiguration<TaskWorkspaceSection>
{
    public void Configure(EntityTypeBuilder<TaskWorkspaceSection> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Key).IsRequired().HasMaxLength(160);
        entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Abstract).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Kind).IsRequired().HasMaxLength(24);
        entity.Property(e => e.Content).IsRequired();
        entity.Property(e => e.AuthorType).IsRequired().HasMaxLength(24);
        entity.Property(e => e.AuthorId).HasMaxLength(128);
        entity.Property(e => e.AuthorRole).HasMaxLength(24);
        entity.Property(e => e.AuthorProfileKey).HasMaxLength(100);
        entity.Property(e => e.Revision).IsRequired();
        entity.HasIndex(e => new { e.TaskId, e.Key }).IsUnique();
        entity.HasIndex(e => new { e.TaskId, e.UpdatedAt });
        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
