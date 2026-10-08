namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for agent templates. Indexed by user.
/// </summary>
public class AgentTemplateConfiguration : IEntityTypeConfiguration<AgentTemplate>
{
    public void Configure(EntityTypeBuilder<AgentTemplate> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Mode).HasConversion<string>().IsRequired();
        entity.HasIndex(e => e.UserId);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.SoloBackendId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.PlannerBackendId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.ActorBackendId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<LocalModelBackend>().WithMany().HasForeignKey(e => e.ReviewBackendId).OnDelete(DeleteBehavior.SetNull);
    }
}
