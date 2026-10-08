namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for project invites. TokenHash is unique.
/// </summary>
public class ProjectInviteConfiguration : IEntityTypeConfiguration<ProjectInvite>
{
    public void Configure(EntityTypeBuilder<ProjectInvite> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
        entity.Property(e => e.TokenHash).IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.ExpiredAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.Email });
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasOne(e => e.Project)
            .WithMany(e => e.Invites)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
