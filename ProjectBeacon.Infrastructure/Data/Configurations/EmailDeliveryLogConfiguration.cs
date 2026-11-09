namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Email;

public sealed class EmailDeliveryLogConfiguration : IEntityTypeConfiguration<EmailDeliveryLog>
{
    public void Configure(EntityTypeBuilder<EmailDeliveryLog> entity)
    {
        entity.HasKey(e=>e.Id);
        entity.Property(e=>e.At).IsRequired();
        entity.Property(e=>e.Recipient).IsRequired().HasMaxLength(320);
        entity.Property(e=>e.Purpose).IsRequired().HasMaxLength(48);
        entity.Property(e=>e.Status).IsRequired().HasMaxLength(16);
        entity.Property(e=>e.ErrorCode).HasMaxLength(80);
        entity.HasIndex(e=>e.At);
    }
}
