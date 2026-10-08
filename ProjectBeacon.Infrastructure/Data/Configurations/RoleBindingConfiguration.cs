namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for pipeline role bindings. Project and role together are unique.
/// </summary>
public class RoleBindingConfiguration : IEntityTypeConfiguration<RoleBinding>
{
    public void Configure(EntityTypeBuilder<RoleBinding> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Role).HasConversion<string>().IsRequired();
        entity.HasIndex(e => new { e.ProjectId, e.Role }).IsUnique();
        entity.HasOne(e => e.ModelBackend)
            .WithMany()
            .HasForeignKey(e => e.ModelBackendId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
