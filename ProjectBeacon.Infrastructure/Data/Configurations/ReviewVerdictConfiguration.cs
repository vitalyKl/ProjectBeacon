namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for review verdicts. Indexed by task and creation time.
/// </summary>
public class ReviewVerdictConfiguration : IEntityTypeConfiguration<ReviewVerdict>
{
    public void Configure(EntityTypeBuilder<ReviewVerdict> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Kind).HasConversion<string>().IsRequired();
        entity.Property(e => e.Note).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.HasIndex(e => new { e.TaskId, e.CreatedAt });
        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
