namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>RLS-scoped immutable evidence for a GitHub PR merged outside the workstation.</summary>
public sealed class RemoteLandingProofConfiguration : IEntityTypeConfiguration<RemoteLandingProof>
{
    public void Configure(EntityTypeBuilder<RemoteLandingProof> entity)
    {
        entity.HasKey(proof => proof.Id);
        entity.Property(proof => proof.GitHubRepository).IsRequired().HasMaxLength(200);
        entity.Property(proof => proof.PullRequestNumber).IsRequired();
        entity.Property(proof => proof.PullRequestUrl).IsRequired().HasMaxLength(500);
        entity.Property(proof => proof.SourceBranch).IsRequired().HasMaxLength(200);
        entity.Property(proof => proof.TargetBranch).IsRequired().HasMaxLength(200);
        entity.Property(proof => proof.SourceSha).IsRequired().HasMaxLength(64);
        entity.Property(proof => proof.MergeCommitSha).IsRequired().HasMaxLength(64);
        entity.Property(proof => proof.RemoteBaseSha).IsRequired().HasMaxLength(64);
        entity.Property(proof => proof.MergedAtUtc).IsRequired();
        entity.Property(proof => proof.VerifiedAtUtc).IsRequired();
        entity.Property(proof => proof.CreatedAtUtc).IsRequired();

        entity.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(proof => new { proof.ProjectId, proof.TaskId })
            .HasPrincipalKey(task => new { task.ProjectId, task.Id })
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(proof => new { proof.ProjectId, proof.TaskId })
            .IsUnique()
            .HasDatabaseName("IX_RemoteLandingProofs_ProjectId_TaskId");
        entity.HasIndex(proof => new { proof.ProjectId, proof.GitHubRepository, proof.PullRequestNumber })
            .IsUnique()
            .HasDatabaseName("IX_RemoteLandingProofs_ProjectId_Repository_PullRequest");
        entity.HasIndex(proof => new { proof.ProjectId, proof.MergeCommitSha });
    }
}
