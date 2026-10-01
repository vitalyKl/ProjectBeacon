namespace ProjectBeacon.Domain.Entities.Evals;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public sealed class EvalRun : Entity, IProjectScoped
{
    public EvalRun() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public string? PairId { get; private set; }
    public EvalCondition Condition { get; private set; }
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int TurnCount { get; private set; }
    public bool? Passed { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? TranscriptRef { get; private set; }

    public static EvalRun Create(Guid projectId, Guid taskId, string? pairId, EvalCondition condition)
    {
        var run = Entity.New<EvalRun>();
        run.ProjectId = projectId;
        run.TaskId = taskId;
        run.PairId = pairId;
        run.Condition = condition;
        run.StartedAt = DateTime.UtcNow;
        return run;
    }

    public void Complete(int promptTokens, int completionTokens, int turnCount, bool? passed, string? transcriptRef = null)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TurnCount = turnCount;
        Passed = passed;
        CompletedAt = DateTime.UtcNow;
        TranscriptRef = transcriptRef;
    }
}
