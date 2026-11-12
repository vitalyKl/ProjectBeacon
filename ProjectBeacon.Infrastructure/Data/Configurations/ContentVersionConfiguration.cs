namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Content;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ContentVersionConfiguration : IEntityTypeConfiguration<ContentVersion>
{
    public void Configure(EntityTypeBuilder<ContentVersion> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TargetKind).HasConversion<string>().IsRequired();
        entity.Property(e => e.ContentJson).HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.ChangedByType).HasMaxLength(32);
        entity.Property(e => e.ChangedById).HasMaxLength(200);
        entity.Property(e => e.At).IsRequired();

        entity.HasOne<Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.ProjectId, e.TargetKind, e.TargetId, e.Revision }).IsUnique();
        entity.HasIndex(e => new { e.ProjectId, e.At });
    }
}
