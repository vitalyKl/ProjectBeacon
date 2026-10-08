namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for chat parts. Indexed by session and sort order, and by session and external id.
/// </summary>
public class ChatPartConfiguration : IEntityTypeConfiguration<ChatPart>
{
    public void Configure(EntityTypeBuilder<ChatPart> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Role).IsRequired().HasMaxLength(40);
        entity.Property(e => e.Kind).IsRequired().HasMaxLength(40);
        entity.Property(e => e.Body).IsRequired();
        entity.Property(e => e.ExternalId).HasMaxLength(200);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.SessionId, e.SortOrder });
        entity.HasIndex(e => new { e.SessionId, e.ExternalId });
    }
}
