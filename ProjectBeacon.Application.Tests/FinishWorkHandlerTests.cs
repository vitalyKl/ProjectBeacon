namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class FinishWorkHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _userId;

    public FinishWorkHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        var user = User.Create("admin", "admin@beacon.local", "hash", isAdmin: true);
        _db.Users.Add(user);
        _db.SaveChanges();
        _projectId = project.Id;
        _userId = user.Id;
        _db.ProjectMembers.Add(ProjectMember.Create(_projectId, _userId, MemberRole.Admin));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Done_FromTodo_ReachesDone()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var proof = Proof(task.Id);
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            ReviewRunId: proof.Id)));

        Assert.True(result.Success, result.Error);
        await _db.Entry(task).ReloadAsync();
        Assert.Equal(TaskItemStatus.Done, task.Status);
    }

    [Fact]
    public async Task Done_WithTranscriptRefOnly_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var proof = Proof(task.Id, "check:exit0");
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(false, 0, 0),
            ReviewTranscriptRef: "check:exit0")));

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithInvalidReviewTranscriptRef_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        _db.ReviewRuns.Add(ReviewRun.Create(_projectId, task.Id, "transcript-abc"));
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0),
            ReviewTranscriptRef: "transcript-WRONG")));

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithoutReviewRun_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0))));

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithTranscriptOnlyCreate_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        _db.ReviewRuns.Add(ReviewRun.Create(_projectId, task.Id, "transcript-only"));
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0),
            ReviewTranscriptRef: "transcript-only")));

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithFailedReviewRun_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        var run = ReviewRun.Start(_projectId, task.Id, ReviewerType.Agent, Guid.NewGuid());
        run.Fail("tests failed");
        _db.ReviewRuns.Add(run);
        await _db.SaveChangesAsync();

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            ReviewRunId: run.Id)));

        Assert.False(result.Success);
        Assert.Contains("did not pass", result.Error);
    }

    private ReviewRun Proof(Guid taskId, string artifact = "check:exit0")
    {
        var run = ReviewRun.Start(_projectId, taskId, ReviewerType.Agent, Guid.NewGuid());
        run.Complete("checked", artifact);
        return run;
    }
}
