namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for orgs.
/// </summary>
public class OrgConfiguration : IEntityTypeConfiguration<Org>
{
    public void Configure(EntityTypeBuilder<Org> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.Description).HasMaxLength(2000);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt);

        entity.HasMany(e => e.Members)
            .WithOne(e => e.Org)
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Projects)
            .WithOne(e => e.Org)
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany<OrgInvite>()
            .WithOne(e => e.Org)
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
