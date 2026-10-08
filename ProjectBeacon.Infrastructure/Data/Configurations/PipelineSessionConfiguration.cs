namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for pipeline sessions. Indexed by task.
/// </summary>
public class PipelineSessionConfiguration : IEntityTypeConfiguration<PipelineSession>
{
    public void Configure(EntityTypeBuilder<PipelineSession> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Role).HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.ExternalSessionId).HasMaxLength(200);
        entity.Property(e => e.LaunchSpec).HasMaxLength(1000);
        entity.Property(e => e.PromptContext).IsRequired();
        entity.HasIndex(e => e.TaskId);
        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
