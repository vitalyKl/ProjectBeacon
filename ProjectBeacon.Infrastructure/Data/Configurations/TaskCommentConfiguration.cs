namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for task comments. Indexed by task and creation time.
/// </summary>
public class TaskCommentConfiguration : IEntityTypeConfiguration<TaskComment>
{
    public void Configure(EntityTypeBuilder<TaskComment> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Content).IsRequired().HasMaxLength(5000);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt);

        entity.HasOne(e => e.Task)
            .WithMany(e => e.Comments)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => new { e.TaskId, e.CreatedAt });
    }
}
