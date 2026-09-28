namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Evals;
using ProjectBeacon.Domain.Enums;

public sealed class EvalRunTests
{
    [Fact]
    public void Create_SetsInitialValues()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var pairId = Guid.NewGuid().ToString();
        var run = EvalRun.Create(projectId, taskId, pairId, EvalCondition.WithBrief);

        Assert.Equal(projectId, run.ProjectId);
        Assert.Equal(taskId, run.TaskId);
        Assert.Equal(pairId, run.PairId);
        Assert.Equal(EvalCondition.WithBrief, run.Condition);
        Assert.True(run.StartedAt > DateTime.MinValue);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.Passed);
    }

    [Fact]
    public void Create_SetsDefaultPairId()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var run = EvalRun.Create(projectId, taskId, null, EvalCondition.WithoutBrief);

        Assert.Null(run.PairId);
        Assert.Equal(EvalCondition.WithoutBrief, run.Condition);
    }

    [Fact]
    public void Create_SetsStartedAtToUtcNow()
    {
        var before = DateTime.UtcNow;
        var run = EvalRun.Create(Guid.NewGuid(), Guid.NewGuid(), "p", EvalCondition.WithBrief);
        var after = DateTime.UtcNow;

        Assert.True(run.StartedAt >= before);
        Assert.True(run.StartedAt <= after);
    }

    [Fact]
    public void Complete_SetsUsageValues()
    {
        var run = EvalRun.Create(Guid.NewGuid(), Guid.NewGuid(), "pair-1", EvalCondition.WithBrief);

        run.Complete(100, 200, 5, true, "ref://log/1");

        Assert.Equal(100, run.PromptTokens);
        Assert.Equal(200, run.CompletionTokens);
        Assert.Equal(5, run.TurnCount);
        Assert.True(run.Passed);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal("ref://log/1", run.TranscriptRef);
    }

    [Fact]
    public void Complete_SetsFailedState()
    {
        var run = EvalRun.Create(Guid.NewGuid(), Guid.NewGuid(), "pair-2", EvalCondition.WithoutBrief);

        run.Complete(50, 10, 2, false);

        Assert.False(run.Passed);
        Assert.Null(run.TranscriptRef);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public void Create_UsesFactoryPattern()
    {
        var run = EvalRun.Create(Guid.NewGuid(), Guid.NewGuid(), null, EvalCondition.WithBrief);

        Assert.NotEqual(Guid.Empty, run.Id);
    }
}
