namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ContextSectionConfiguration : IEntityTypeConfiguration<ContextSection>
{
    public void Configure(EntityTypeBuilder<ContextSection> entity)
    {
        entity.Property(e => e.Revision).HasDefaultValue(1).IsConcurrencyToken().IsRequired();
    }
}
