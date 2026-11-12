namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Agents;
using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>Maps per-user overrides of built-in subtask agent profiles.</summary>
public sealed class AgentProfileConfiguration : IEntityTypeConfiguration<AgentProfile>
{
    public void Configure(EntityTypeBuilder<AgentProfile> entity)
    {
        entity.HasKey(profile => profile.Id);
        entity.Property(profile => profile.Key).IsRequired().HasMaxLength(100);
        entity.Property(profile => profile.Name).IsRequired().HasMaxLength(120);
        entity.Property(profile => profile.Description).IsRequired().HasMaxLength(300);
        entity.Property(profile => profile.Instruction).IsRequired().HasMaxLength(8000);
        entity.Property(profile => profile.Mode).IsRequired().HasMaxLength(16);
        entity.Property(profile => profile.ToolsJson).IsRequired().HasColumnType("text");
        entity.Property(profile => profile.PathPolicy).IsRequired().HasMaxLength(24);
        entity.Property(profile => profile.DefaultPathsJson).IsRequired().HasColumnType("text");
        entity.Property(profile => profile.SizeHint).IsRequired().HasMaxLength(200);
        entity.Property(profile => profile.Effort).IsRequired().HasMaxLength(24);
        entity.Property(profile => profile.ApplyPolicy).IsRequired().HasMaxLength(16).HasDefaultValue("Review");
        entity.HasIndex(profile => profile.UserId);
        entity.HasIndex(profile => new { profile.UserId, profile.Key }).IsUnique();
        entity.HasOne<User>().WithMany().HasForeignKey(profile => profile.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
