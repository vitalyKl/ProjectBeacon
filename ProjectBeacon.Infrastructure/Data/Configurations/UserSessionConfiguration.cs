namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for login sessions. Indexed by user and active flag.
/// </summary>
public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.IpAddress).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.UserId, e.IsActive });
    }
}
