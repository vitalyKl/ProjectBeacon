namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for constraints. Indexed by project, kind, and status.
/// </summary>
public class ConstraintConfiguration : IEntityTypeConfiguration<Constraint>
{
    public void Configure(EntityTypeBuilder<Constraint> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Body).IsRequired();
        entity.Property(e => e.ScopePath).HasMaxLength(500).HasDefaultValue("");
        entity.Property(e => e.Kind).HasConversion<string>().IsRequired();
        entity.Property(e => e.Status).HasConversion<string>().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.Kind, e.Status });
    }
}
