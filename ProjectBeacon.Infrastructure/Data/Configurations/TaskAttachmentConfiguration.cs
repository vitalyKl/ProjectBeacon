namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskAttachmentConfiguration : IEntityTypeConfiguration<TaskAttachment>
{
    public void Configure(EntityTypeBuilder<TaskAttachment> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.FileName).IsRequired().HasMaxLength(255);
        entity.Property(e => e.ContentType).IsRequired().HasMaxLength(160);
        entity.Property(e => e.Length).IsRequired();
        entity.Property(e => e.StorageKey).IsRequired().HasMaxLength(64);
        entity.Property(e => e.CreatedByType).IsRequired().HasMaxLength(24);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasOne<TaskItem>().WithMany().HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(e => new { e.TaskId, e.CreatedAt });
        entity.HasIndex(e => e.ProjectId);
        entity.HasIndex(e => e.StorageKey).IsUnique();
    }
}
