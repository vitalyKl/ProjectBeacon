namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class OrgMemberConfiguration : IEntityTypeConfiguration<OrgMember>
{
    public void Configure(EntityTypeBuilder<OrgMember> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.JoinedAt).IsRequired();
        entity.HasIndex(e => new { e.OrgId, e.UserId }).IsUnique();
        entity.HasOne(e => e.Org)
            .WithMany(e => e.Members)
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
