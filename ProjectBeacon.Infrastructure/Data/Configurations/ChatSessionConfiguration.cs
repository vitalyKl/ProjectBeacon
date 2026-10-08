namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for chat sessions. Indexed by project and creation time.
/// </summary>
public class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.ExternalSessionId).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
        entity.Property(e => e.LocalRoot).IsRequired().HasMaxLength(1000);
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
        entity.HasMany(e => e.Parts)
            .WithOne(e => e.Session)
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
