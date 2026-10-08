namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class LocalModelBackendConfiguration : IEntityTypeConfiguration<LocalModelBackend>
{
    public void Configure(EntityTypeBuilder<LocalModelBackend> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.BackendType).HasConversion<string>().IsRequired();
        entity.Property(e => e.LaunchCommand).IsRequired().HasMaxLength(1000);
        entity.Property(e => e.ContextSize).IsRequired();
        entity.Property(e => e.Ttl).IsRequired();
        entity.Property(e => e.Concurrent).IsRequired();
        entity.Ignore(e => e.ExtraFlags);
        entity.Property(e => e.ExtraFlagsJson).IsRequired();
        entity.Property(e => e.Note).HasMaxLength(2000);
        entity.Property(e => e.OpenCodeModel).HasMaxLength(200);
        entity.Property(e => e.UpdatedAt);
        entity.HasIndex(e => e.UserId);
    }
}
