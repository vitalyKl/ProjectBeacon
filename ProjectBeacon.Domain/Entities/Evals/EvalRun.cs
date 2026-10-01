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
    public string? Model { get; private set; }
    public double? Temperature { get; private set; }
    public string? ReasoningEffort { get; private set; }
    public string? ToolPermissions { get; private set; }
    public int? TimeoutSeconds { get; private set; }
    public string? RepoRevision { get; private set; }
    public string? CheckCommand { get; private set; }
    public int? CheckExitCode { get; private set; }
    public string? CheckOutput { get; private set; }
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

    public void Pin(string model, double temperature, string reasoningEffort, string toolPermissions, int timeoutSeconds, string repoRevision, string checkCommand)
    {
        Model = model.Trim();
        Temperature = temperature;
        ReasoningEffort = reasoningEffort.Trim();
        ToolPermissions = toolPermissions.Trim();
        TimeoutSeconds = timeoutSeconds;
        RepoRevision = repoRevision.Trim();
        CheckCommand = checkCommand.Trim();
    }

    public void Complete(int promptTokens, int completionTokens, int turnCount, int? checkExitCode, string? transcriptRef = null, string? checkOutput = null)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TurnCount = turnCount;
        CheckExitCode = checkExitCode;
        CheckOutput = Truncate(checkOutput);
        Passed = checkExitCode == 0;
        CompletedAt = DateTime.UtcNow;
        TranscriptRef = transcriptRef;
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[..4000];
    }
}
