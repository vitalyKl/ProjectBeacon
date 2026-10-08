namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for project labels.
/// </summary>
public class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
        entity.Property(e => e.Color).IsRequired().HasMaxLength(20);
        entity.Property(e => e.PathPrefix).HasMaxLength(500).HasDefaultValue("");
        entity.HasMany(e => e.Paths)
            .WithOne(e => e.Label)
            .HasForeignKey(e => e.LabelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
