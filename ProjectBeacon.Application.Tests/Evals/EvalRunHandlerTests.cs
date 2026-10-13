namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Evals;
using Domain.Enums;
using Evals;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class EvalRunHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly BeaconDbFactory _factory;

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _taskId = Guid.NewGuid();

    public EvalRunHandlerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new BeaconDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection).Options);
        _unscoped = TenantScope.EnterUnscoped();
        _db.Database.EnsureCreated();
        _factory = HandlerSqlite.Factory(_connection);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _unscoped.Dispose();
    }

    [Fact]
    public async Task RecordEvalRun_CreatesRun()
    {
        var handler = new RecordEvalRunHandler(_factory);

        var result = await handler.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, _taskId, "pair-1", EvalCondition.WithBrief)));

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(_projectId, result.Value.ProjectId);
        Assert.Equal(_taskId, result.Value.TaskId);
        Assert.Equal("pair-1", result.Value.PairId);
        Assert.Equal("WithBrief", result.Value.Condition);
        Assert.Null(result.Value.CompletedAt);
    }

    [Fact]
    public async Task CompleteEvalRun_CompletesRun()
    {
        var record = new RecordEvalRunHandler(_factory);
        var recordResult = await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, _taskId, "pair-1", EvalCondition.WithBrief)));
        Assert.True(recordResult.Success);

        var complete = new CompleteEvalRunHandler(_factory);
        var completeResult = await complete.HandleAsync(new CompleteEvalRunCommand(
            new CompleteEvalRunRequest(recordResult.Value!.Id, 100, 200, 5, 0, "ref://1")));

        Assert.True(completeResult.Success);
        Assert.Equal(100, completeResult.Value!.PromptTokens);
        Assert.Equal(200, completeResult.Value.CompletionTokens);
        Assert.Equal(5, completeResult.Value.TurnCount);
        Assert.True(completeResult.Value.Passed);
        Assert.NotNull(completeResult.Value.CompletedAt);
    }

    [Fact]
    public async Task CompleteEvalRun_FailsForNonExistentRun()
    {
        var complete = new CompleteEvalRunHandler(_factory);

        var result = await complete.HandleAsync(new CompleteEvalRunCommand(
            new CompleteEvalRunRequest(Guid.NewGuid(), 100, 200, 5, 0, "ref://1")));

        Assert.False(result.Success);
        Assert.Equal("EvalRun not found.", result.Error);
    }

    [Fact]
    public async Task ListEvalRuns_ReturnsAllRuns()
    {
        var record = new RecordEvalRunHandler(_factory);
        var list = new ListEvalRunsHandler(_factory);

        await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, _taskId, null, EvalCondition.WithBrief)));
        await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, _taskId, null, EvalCondition.WithoutBrief)));

        var result = await list.HandleAsync(new ListEvalRunsRequest(_projectId));

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
    }

    [Fact]
    public async Task ListEvalRuns_FiltersByTaskId()
    {
        var record = new RecordEvalRunHandler(_factory);
        var list = new ListEvalRunsHandler(_factory);

        var otherTask = Guid.NewGuid();
        await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, _taskId, null, EvalCondition.WithBrief)));
        await record.HandleAsync(new RecordEvalRunCommand(
            new RecordEvalRunRequest(_projectId, otherTask, null, EvalCondition.WithBrief)));

        var result = await list.HandleAsync(new ListEvalRunsRequest(_projectId, _taskId));

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal(_taskId, result.Value![0].TaskId);
    }

    [Fact]
    public async Task ListEvalRuns_ReturnsEmptyForOtherProject()
    {
        var list = new ListEvalRunsHandler(_factory);

        var result = await list.HandleAsync(new ListEvalRunsRequest(Guid.NewGuid()));

        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }
}
