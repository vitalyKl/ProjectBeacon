namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Billing;

public sealed class BillingPlanConfiguration : IEntityTypeConfiguration<BillingPlan>
{
    public void Configure(EntityTypeBuilder<BillingPlan> entity)
    {
        entity.HasKey(plan => plan.Id);
        entity.Property(plan => plan.Id).ValueGeneratedNever();
        entity.Property(plan => plan.Key).IsRequired().HasMaxLength(64);
        entity.HasIndex(plan => plan.Key).IsUnique();
        entity.Property(plan => plan.Name).IsRequired().HasMaxLength(120);
        entity.Property(plan => plan.Cadence).IsRequired();
        entity.Property(plan => plan.PriceMinor);
        entity.Property(plan => plan.Currency).HasMaxLength(3);
        entity.Property(plan => plan.LimitsJson).IsRequired().HasColumnType("jsonb");
        entity.Property(plan => plan.FeaturesJson).IsRequired().HasColumnType("jsonb");
        entity.Property(plan => plan.IsActive).IsRequired();
        entity.Property(plan => plan.CreatedAtUtc).IsRequired();
        entity.Property(plan => plan.UpdatedAtUtc).IsRequired();
        entity.HasIndex(plan => plan.IsActive);
    }
}
