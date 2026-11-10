namespace ProjectBeacon.Infrastructure.Data.Configurations;

using ProjectBeacon.Domain.Entities.Content;
using ProjectBeacon.Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ProposalConfiguration : IEntityTypeConfiguration<Proposal>
{
    public void Configure(EntityTypeBuilder<Proposal> entity)
    {
        entity.HasKey(proposal => proposal.Id);
        entity.Property(proposal => proposal.TargetKind).HasConversion<string>().IsRequired();
        entity.Property(proposal => proposal.Operation).HasConversion<string>().IsRequired();
        entity.Property(proposal => proposal.Status).HasConversion<string>().IsRequired();
        entity.Property(proposal => proposal.AuthorKind).HasConversion<string>().IsRequired();
        entity.Property(proposal => proposal.BaseHash).HasMaxLength(64).IsRequired();
        entity.Property(proposal => proposal.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.Property(proposal => proposal.RationaleMarkdown).HasMaxLength(Proposal.MaxRationaleLength).IsRequired();
        entity.Property(proposal => proposal.AuthorProfileKey).HasMaxLength(100);
        entity.Property(proposal => proposal.AuthorRole).HasMaxLength(32);
        entity.Property(proposal => proposal.IdempotencyKey).HasMaxLength(Proposal.MaxIdempotencyKeyLength).IsRequired();
        entity.Property(proposal => proposal.DecisionNote).HasMaxLength(Proposal.MaxRationaleLength);
        entity.Property(proposal => proposal.AcceptedPayloadJson).HasColumnType("jsonb");
        entity.Property(proposal => proposal.StateRevision).IsConcurrencyToken();

        entity.HasOne<Project>()
            .WithMany()
            .HasForeignKey(proposal => proposal.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(proposal => new { proposal.ProjectId, proposal.Status, proposal.CreatedAt });
        entity.HasIndex(proposal => new { proposal.ProjectId, proposal.TaskId, proposal.CreatedAt });
        entity.HasIndex(proposal => new { proposal.ProjectId, proposal.IdempotencyKey }).IsUnique();
        entity.HasIndex(proposal => new
            {
                proposal.ProjectId,
                proposal.SubtaskId,
                proposal.TargetKind,
                proposal.TargetId
            })
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending' AND \"SubtaskId\" IS NOT NULL");
    }
}
