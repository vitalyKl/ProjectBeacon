namespace ProjectBeacon.Infrastructure.Data.Configurations;

using Domain.Entities.Evals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
/// <summary>
/// EF mapping for eval runs. Indexed by project and task, and by pair id.
/// </summary>
public class EvalRunConfiguration : IEntityTypeConfiguration<EvalRun>
{
    public void Configure(EntityTypeBuilder<EvalRun> entity)
    {
        entity.HasKey(e => e.Id);
        entity.Property(e => e.PairId).HasMaxLength(100);
        entity.Property(e => e.Condition).HasConversion<string>().IsRequired();
        entity.Property(e => e.PromptTokens).IsRequired();
        entity.Property(e => e.CompletionTokens).IsRequired();
        entity.Property(e => e.TurnCount).IsRequired();
        entity.Property(e => e.Model).HasMaxLength(200);
        entity.Property(e => e.ReasoningEffort).HasMaxLength(50);
        entity.Property(e => e.ToolPermissions).HasMaxLength(500);
        entity.Property(e => e.RepoRevision).HasMaxLength(100);
        entity.Property(e => e.CheckCommand).HasMaxLength(500);
        entity.Property(e => e.CheckOutput).HasMaxLength(4000);
        entity.Property(e => e.StartedAt).IsRequired();
        entity.Property(e => e.CompletedAt);
        entity.Property(e => e.TranscriptRef).HasMaxLength(500);
        entity.HasIndex(e => new { e.ProjectId, e.TaskId });
        entity.HasIndex(e => e.PairId);
    }
}
