namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class OrgInviteConfiguration : IEntityTypeConfiguration<OrgInvite>
{
    public void Configure(EntityTypeBuilder<OrgInvite> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
        entity.Property(e => e.TokenHash).IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.ExpiredAt).IsRequired();
        entity.HasIndex(e => new { e.OrgId, e.Email });
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasOne(e => e.Org)
            .WithMany(e => e.Invites)
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
