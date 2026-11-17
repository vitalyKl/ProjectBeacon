namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Projects;

public sealed class ProjectGitPolicyConfiguration : IEntityTypeConfiguration<ProjectGitPolicy>
{
    public void Configure(EntityTypeBuilder<ProjectGitPolicy> entity)
    {
        entity.HasKey(policy => policy.Id);
        entity.Property(policy => policy.AutonomyLevel).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(policy => policy.DirtyCheckoutPolicy).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(policy => policy.AutoRevertPolicy).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(policy => policy.MaxChangedFiles).IsRequired();
        entity.Property(policy => policy.MaxChangedLines).IsRequired();
        entity.Property(policy => policy.AdditionalProtectedPathsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(policy => policy.Revision).IsConcurrencyToken().IsRequired();
        entity.Property(policy => policy.UpdatedByUserId);
        entity.Property(policy => policy.CreatedAt).IsRequired();
        entity.Property(policy => policy.UpdatedAt).IsRequired();

        entity.HasOne<Project>()
            .WithMany()
            .HasForeignKey(policy => policy.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(policy => policy.ProjectId).IsUnique();
    }
}
