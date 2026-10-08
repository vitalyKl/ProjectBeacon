namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class LabelPathConfiguration : IEntityTypeConfiguration<LabelPath>
{
    public void Configure(EntityTypeBuilder<LabelPath> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Path).IsRequired().HasMaxLength(500);
        entity.HasIndex(e => new { e.LabelId, e.Path }).IsUnique();
    }
}
