namespace ProjectBeacon.Application.Tests;

using Application.Evals;
using Application.Reports;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ContextCostReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public ContextCostReportTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _unscoped.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private ContextCostReportHandler Handler() => new(Factory());

    private async Task<(Project Project, TaskItem Task)> SeedProjectAndTaskAsync(string projectName, string taskTitle)
    {
        using (TenantScope.EnterUnscoped())
        {
            var login = $"user-{Guid.NewGuid():N}";
            var user = User.Create(login, $"{login}@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();

            var project = Project.Create(projectName, null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));

            var task = TaskItem.Create(taskTitle, project.Id);
            _db.Tasks.Add(task);

            await _db.SaveChangesAsync();
            return (project, task);
        }
    }

    private async Task<Guid> RecordAndCompleteAsync(Guid projectId, Guid taskId, string pairId, EvalCondition condition,
        int promptTokens, int completionTokens, int turnCount, bool passed)
    {
        var record = new RecordEvalRunHandler(Factory());
        var rec = await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(projectId, taskId, pairId, condition)));
        Assert.True(rec.Success, rec.Error);

        var complete = new CompleteEvalRunHandler(Factory());
        var done = await complete.HandleAsync(new CompleteEvalRunCommand(
            new CompleteEvalRunRequest(rec.Value!.Id, promptTokens, completionTokens, turnCount, passed ? 0 : 1, "ref://test")));
        Assert.True(done.Success, done.Error);
        return rec.Value.Id;
    }

    private async Task<Guid> RecordAsync(Guid projectId, Guid taskId, string pairId, EvalCondition condition)
    {
        var record = new RecordEvalRunHandler(Factory());
        var rec = await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(projectId, taskId, pairId, condition)));
        Assert.True(rec.Success, rec.Error);
        return rec.Value!.Id;
    }

    [Fact]
    public async Task Report_PairedCompletedRuns_ReturnsDeltas()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P1", "Test task");
        var pairId = Guid.NewGuid().ToString("n");

        await RecordAndCompleteAsync(project.Id, task.Id, pairId, EvalCondition.WithBrief, 100, 200, 5, true);
        await RecordAndCompleteAsync(project.Id, task.Id, pairId, EvalCondition.WithoutBrief, 50, 100, 3, false);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, project.Id));

        Assert.True(result.Success, result.Error);
        Assert.Equal(task.Id, result.Value!.TaskId);
        Assert.Equal(project.Id, result.Value.ProjectId);
        Assert.Equal("Test task", result.Value.TaskTitle);
        Assert.Equal(1, result.Value.PairCount);
        Assert.Single(result.Value.Pairs);

        var pair = result.Value.Pairs[0];
        Assert.Equal(pairId, pair.PairId);
        Assert.NotNull(pair.WithBrief);
        Assert.NotNull(pair.WithoutBrief);
        Assert.Equal(100, pair.WithBrief!.PromptTokens);
        Assert.Equal(200, pair.WithBrief.CompletionTokens);
        Assert.Equal(5, pair.WithBrief.TurnCount);
        Assert.True(pair.WithBriefPassed);
        Assert.Equal(50, pair.WithoutBrief!.PromptTokens);
        Assert.Equal(100, pair.WithoutBrief.CompletionTokens);
        Assert.Equal(3, pair.WithoutBrief.TurnCount);
        Assert.False(pair.WithoutBriefPassed);

        Assert.Equal(50, pair.PromptTokensDelta);
        Assert.Equal(100, pair.CompletionTokensDelta);
        Assert.Equal(150, pair.TotalTokensDelta);
        Assert.Equal(2, pair.TurnCountDelta);
    }

    [Fact]
    public async Task Report_NoPairedRuns_ReturnsEmptyPairs()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P2", "Lonely task");
        await RecordAsync(project.Id, task.Id, null, EvalCondition.WithBrief);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, project.Id));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Value!.PairCount);
        Assert.Empty(result.Value.Pairs);
    }

    [Fact]
    public async Task Report_TaskNotFound_Fails()
    {
        await SeedProjectAndTaskAsync("P3", "Some task");

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(Guid.NewGuid()));

        Assert.False(result.Success);
        Assert.Equal("Task not found.", result.Error);
    }

    [Fact]
    public async Task Report_PartialPair_DeltasAreNull()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P4", "Partial task");
        var pairId = Guid.NewGuid().ToString("n");

        await RecordAndCompleteAsync(project.Id, task.Id, pairId, EvalCondition.WithBrief, 100, 200, 5, true);
        await RecordAsync(project.Id, task.Id, pairId, EvalCondition.WithoutBrief);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, project.Id));

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value!.PairCount);
        var pair = result.Value.Pairs[0];
        Assert.NotNull(pair.WithBrief);
        Assert.NotNull(pair.WithoutBrief);
        Assert.Null(pair.PromptTokensDelta);
        Assert.Null(pair.CompletionTokensDelta);
        Assert.Null(pair.TotalTokensDelta);
        Assert.Null(pair.TurnCountDelta);
    }

    [Fact]
    public async Task Report_MultiplePairs_ReturnsAllPairs()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P5", "Multi pair task");
        var pair1 = Guid.NewGuid().ToString("n");
        var pair2 = Guid.NewGuid().ToString("n");

        await RecordAndCompleteAsync(project.Id, task.Id, pair1, EvalCondition.WithBrief, 100, 200, 5, true);
        await RecordAndCompleteAsync(project.Id, task.Id, pair1, EvalCondition.WithoutBrief, 50, 100, 3, true);
        await RecordAndCompleteAsync(project.Id, task.Id, pair2, EvalCondition.WithBrief, 80, 160, 4, false);
        await RecordAndCompleteAsync(project.Id, task.Id, pair2, EvalCondition.WithoutBrief, 40, 80, 2, false);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, project.Id));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Value!.PairCount);
        Assert.Equal(2, result.Value.Pairs.Count);
    }

    [Fact]
    public async Task Report_WrongProject_ReturnsFailure()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P6", "Scoped task");
        var (_, otherProject) = await SeedProjectAndTaskAsync("P6b", "Other task");
        var pairId = Guid.NewGuid().ToString("n");
        await RecordAndCompleteAsync(project.Id, task.Id, pairId, EvalCondition.WithBrief, 100, 200, 5, true);
        await RecordAndCompleteAsync(project.Id, task.Id, pairId, EvalCondition.WithoutBrief, 50, 100, 3, false);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, otherProject.Id));

        Assert.False(result.Success);
        Assert.Equal("Task not found.", result.Error);
    }

    [Fact]
    public async Task Report_UncompletedRuns_ReturnsPairWithNullDeltas()
    {
        var (project, task) = await SeedProjectAndTaskAsync("P7", "Pending task");
        var pairId = Guid.NewGuid().ToString("n");

        await RecordAsync(project.Id, task.Id, pairId, EvalCondition.WithBrief);
        await RecordAsync(project.Id, task.Id, pairId, EvalCondition.WithoutBrief);

        var result = await Handler().HandleAsync(new GetContextCostReportRequest(task.Id, project.Id));

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value!.PairCount);
        var pair = result.Value.Pairs[0];
        Assert.NotNull(pair.WithBrief);
        Assert.NotNull(pair.WithoutBrief);
        Assert.Null(pair.PromptTokensDelta);
        Assert.Null(pair.CompletionTokensDelta);
        Assert.Null(pair.TotalTokensDelta);
        Assert.Null(pair.TurnCountDelta);
    }
}
