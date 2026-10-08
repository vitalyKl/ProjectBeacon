namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for password-reset tokens. TokenHash is unique.
/// </summary>
public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TokenHash).IsRequired();
        entity.Property(e => e.ExpiresAt).IsRequired();
        entity.HasIndex(e => e.TokenHash).IsUnique();
        entity.HasIndex(e => e.UserId);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
