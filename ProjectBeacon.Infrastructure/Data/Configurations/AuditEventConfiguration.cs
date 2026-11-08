namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectBeacon.Domain.Entities.Audit;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.At).IsRequired();
        entity.Property(e => e.ActorKind).HasConversion<string>().HasMaxLength(24).IsRequired();
        entity.Property(e => e.ActorUserId);
        entity.Property(e => e.ActorTokenId);
        entity.Property(e => e.ActorDeviceId);
        entity.Property(e => e.Action).IsRequired().HasMaxLength(160);
        entity.Property(e => e.TargetType).IsRequired().HasMaxLength(80);
        entity.Property(e => e.TargetId).HasMaxLength(128);
        entity.Property(e => e.OrgId);
        entity.Property(e => e.ProjectId);
        entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(e => e.Ip).HasMaxLength(64);
        entity.Property(e => e.UserAgent).HasMaxLength(512);
        entity.Property(e => e.TraceId).HasMaxLength(128);
        entity.Property(e => e.DetailsJson).HasColumnType("jsonb");

        entity.HasIndex(e => new { e.OrgId, e.At });
        entity.HasIndex(e => new { e.ProjectId, e.At });
        entity.HasIndex(e => new { e.Action, e.At });
        entity.HasIndex(e => new { e.TargetType, e.TargetId });
    }
}
