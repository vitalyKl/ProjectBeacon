namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ContextSectionConfiguration : IEntityTypeConfiguration<ContextSection>
{
    public void Configure(EntityTypeBuilder<ContextSection> entity)
    {
        entity.Property(e => e.Revision).HasDefaultValue(1).IsConcurrencyToken().IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.SourcePath })
            .IsUnique()
            .HasDatabaseName("IX_ContextSections_ImportedFileIdentity")
            .HasFilter("\"Source\" <> 0 AND \"SourcePath\" IS NOT NULL AND \"SourcePath\" <> '' AND \"ScopeType\" = 0 AND \"RepoId\" IS NULL AND \"TaskId\" IS NULL AND \"Path\" = ''");
    }
}
