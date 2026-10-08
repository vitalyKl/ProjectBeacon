namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for API tokens. TokenHash is unique.
/// </summary>
public class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.TokenHash).IsRequired();
        entity.Property(e => e.TokenPrefix).HasMaxLength(20);
        entity.Property(e => e.Capabilities).HasConversion<long>().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasOne(e => e.Project)
            .WithMany(e => e.ApiTokens)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
