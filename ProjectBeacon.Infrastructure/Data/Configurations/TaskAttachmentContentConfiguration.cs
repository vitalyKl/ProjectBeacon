namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskAttachmentContentConfiguration : IEntityTypeConfiguration<TaskAttachmentContent>
{
    public void Configure(EntityTypeBuilder<TaskAttachmentContent> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Content).IsRequired().HasColumnType("bytea");
        entity.HasOne<TaskAttachment>()
            .WithOne()
            .HasForeignKey<TaskAttachmentContent>(e => e.TaskAttachmentId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(e => e.TaskAttachmentId).IsUnique();
        entity.HasIndex(e => e.ProjectId);
    }
}
