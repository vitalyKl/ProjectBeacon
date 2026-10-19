namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Agents;
using Domain.Entities.Chat;
using Domain.Entities.Context;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Reports;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

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
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var proof = Proof(task.Id);
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            ReviewRunId: proof.Id)), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Done, task.Status);
    }

    [Fact]
    public async Task Done_WithTranscriptRefOnly_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var proof = Proof(task.Id, "check:exit0");
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(false, 0, 0),
            ReviewTranscriptRef: "check:exit0")), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithInvalidReviewTranscriptRef_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ReviewRuns.Add(ReviewRun.Create(_projectId, task.Id, "transcript-abc"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0),
            ReviewTranscriptRef: "transcript-WRONG")), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithoutReviewRun_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0))), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("completed review run", result.Error);
    }

    [Fact]
    public async Task Done_WithTranscriptOnlyCreate_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        _db.ReviewRuns.Add(ReviewRun.Create(_projectId, task.Id, "transcript-only"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            new FinishWorkReview(true, 0, 0),
            ReviewTranscriptRef: "transcript-only")), TestContext.Current.CancellationToken);

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
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(),
            "done",
            "ok",
            _userId.ToString(),
            ReviewRunId: run.Id)), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("did not pass", result.Error);
    }

    [Fact]
    public async Task Done_ReplayWithDifferentReviewRun_IsIdempotent_KeepsFirstNotes()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var firstProof = Proof(task.Id);
        var secondProof = Proof(task.Id);
        _db.ReviewRuns.Add(firstProof);
        _db.ReviewRuns.Add(secondProof);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var first = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "first notes", _userId.ToString(), ReviewRunId: firstProof.Id)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // A repeated done — even citing a different valid review run — must not write again.
        var replay = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "second notes", _userId.ToString(), ReviewRunId: secondProof.Id)), TestContext.Current.CancellationToken);
        Assert.True(replay.Success, replay.Error);

        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Equal("first notes", task.ReviewNotes);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_SameOutputReplay_IsSuppressed()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var first = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // The same failure report again: the effect is already in place, so it is a no-op.
        var replay = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(replay.Success, replay.Error);

        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal("Failed: X", task.Description);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_PrefixCollision_IsNotSuppressed()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var first = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // "Failed: XY" is not a "\n\n" segment of "Failed: X", so it must be appended.
        var second = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "XY", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(second.Success, second.Error);

        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal("Failed: X\n\nFailed: XY", task.Description);
        Assert.Equal(2, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Done_ReplayWithFailedReviewRun_Fails()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        var proof = Proof(task.Id);
        var failedRun = ReviewRun.Start(_projectId, task.Id, ReviewerType.Agent, Guid.NewGuid());
        failedRun.Fail("tests failed");
        _db.ReviewRuns.Add(proof);
        _db.ReviewRuns.Add(failedRun);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        var first = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "ok", _userId.ToString(), ReviewRunId: proof.Id)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // The task is already done, but the cited review run failed, so validation rejects it.
        var replay = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "ok", _userId.ToString(), ReviewRunId: failedRun.Id)), TestContext.Current.CancellationToken);
        Assert.False(replay.Success);
        Assert.Contains("did not pass", replay.Error);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_StaleRevisionAfterRivalClaim_Conflicts_NoSideEffects()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A rival claim commits after the handler reads the row: status InProgress, revision bumped.
        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var db = RivalCommittingDb.Wrap(inner, () => inner.Tasks.Where(t => t.Id == task.Id).ExecuteUpdate(s => s
            .SetProperty(t => t.Status, TaskItemStatus.InProgress)
            .SetProperty(t => t.StateRevision, t => t.StateRevision + 1)));

        var handler = new FinishWorkHandler(new FixedDbFactory(db));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Conflict, result.Kind);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
        Assert.Null(task.Description);
        Assert.Equal(1, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Done_StaleRevisionAfterRivalDone_IdempotentOk_NoOverwrite()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var proof = Proof(task.Id);
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A rival done commits after the handler reads the row.
        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var db = RivalCommittingDb.Wrap(inner, () => inner.Tasks.Where(t => t.Id == task.Id).ExecuteUpdate(s => s
            .SetProperty(t => t.Status, TaskItemStatus.Done)
            .SetProperty(t => t.CompletedAt, DateTime.UtcNow)
            .SetProperty(t => t.ReviewNotes, "rival notes")
            .SetProperty(t => t.StateRevision, t => t.StateRevision + 1)));

        var handler = new FinishWorkHandler(new FixedDbFactory(db));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "my notes", _userId.ToString(), ReviewRunId: proof.Id)), TestContext.Current.CancellationToken);

        // The effect is already in place: idempotent Ok, the rival's notes survive, no comment.
        Assert.True(result.Success, result.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Equal("rival notes", task.ReviewNotes);
        Assert.Equal(1, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_SameOutputAfterRivalCommit_IdempotentOk_NoDuplicate()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A rival failed report commits after the handler reads the row.
        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var db = RivalCommittingDb.Wrap(inner, () => inner.Tasks.Where(t => t.Id == task.Id).ExecuteUpdate(s => s
            .SetProperty(t => t.Description, "Failed: X")
            .SetProperty(t => t.StateRevision, t => t.StateRevision + 1)));

        var handler = new FinishWorkHandler(new FixedDbFactory(db));
        var result = await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);

        // The re-read shows Todo plus the exact note: idempotent Ok, nothing duplicated.
        Assert.True(result.Success, result.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal("Failed: X", task.Description);
        Assert.Equal(1, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_WinnerBumpsStateRevision_EachLandingReportOnce()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new FinishWorkHandler(HandlerSqlite.Factory(_connection));
        Assert.True((await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken)).Success);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, task.StateRevision);

        // A different report reads the fresh revision and lands: the token bumps again.
        Assert.True((await handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "Y", _userId.ToString())), TestContext.Current.CancellationToken)).Success);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, task.StateRevision);
        Assert.Equal("Failed: X\n\nFailed: Y", task.Description);
        Assert.Equal(2, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Done_CommentInsertFails_RollsBackState_RetryLandsOnce()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var proof = Proof(task.Id);
        _db.ReviewRuns.Add(proof);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The comment insert fails after the guarded state write has run in the transaction.
        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var handler = new FinishWorkHandler(new FixedDbFactory(CommentSaveFailingDb.Wrap(inner)));
        await Assert.ThrowsAsync<DbUpdateException>(() => handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "done", "ok", _userId.ToString(), ReviewRunId: proof.Id)), TestContext.Current.CancellationToken));

        // The state write rolled back with the failed comment: nothing landed.
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.ReviewNotes);
        Assert.Null(task.CompletedAt);
        Assert.Equal(0, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));

        // The retry lands exactly once: Done, the notes, one revision bump, one comment.
        var retry = await new FinishWorkHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                task.Id.ToString(), "done", "ok", _userId.ToString(), ReviewRunId: proof.Id)), TestContext.Current.CancellationToken);
        Assert.True(retry.Success, retry.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Done, task.Status);
        Assert.Equal("ok", task.ReviewNotes);
        Assert.Equal(1, task.StateRevision);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_CommentInsertFails_RollsBackNote_RetryLandsOnce()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var handler = new FinishWorkHandler(new FixedDbFactory(CommentSaveFailingDb.Wrap(inner)));
        await Assert.ThrowsAsync<DbUpdateException>(() => handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken));

        // The note append and the status write rolled back with the failed comment.
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.Description);
        Assert.Equal(0, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));

        // The retry lands the note and exactly one comment.
        var retry = await new FinishWorkHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                task.Id.ToString(), "failed", "X", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(retry.Success, retry.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal("Failed: X", task.Description);
        Assert.Equal(1, task.StateRevision);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Skipped_CommentInsertFails_RollsBackNote_RetryLandsOnce()
    {
        var task = TaskItem.Create("t", _projectId);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var inner = HandlerSqlite.Factory(_connection).CreateDbContext();
        var handler = new FinishWorkHandler(new FixedDbFactory(CommentSaveFailingDb.Wrap(inner)));
        await Assert.ThrowsAsync<DbUpdateException>(() => handler.HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
            task.Id.ToString(), "skipped", "reason", _userId.ToString())), TestContext.Current.CancellationToken));

        // The note append rolled back with the failed comment; status never moves for skipped.
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Null(task.Description);
        Assert.Equal(0, task.StateRevision);
        Assert.Equal(0, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));

        // The retry lands the note and exactly one comment; the revision stays untouched.
        var retry = await new FinishWorkHandler(HandlerSqlite.Factory(_connection))
            .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                task.Id.ToString(), "skipped", "reason", _userId.ToString())), TestContext.Current.CancellationToken);
        Assert.True(retry.Success, retry.Error);
        await _db.Entry(task).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.Todo, task.Status);
        Assert.Equal("Note: reason", task.Description);
        Assert.Equal(0, task.StateRevision);
        Assert.Equal(1, await _db.TaskComments.CountAsync(c => c.TaskId == task.Id, TestContext.Current.CancellationToken));
    }

    private ReviewRun Proof(Guid taskId, string artifact = "check:exit0")
    {
        var run = ReviewRun.Start(_projectId, taskId, ReviewerType.Agent, Guid.NewGuid());
        run.Complete("checked", artifact);
        return run;
    }

    /// <summary>
    /// Forwards every call to the wrapped context. On the first access to <c>Database</c> —
    /// after the handler read the row, before its guarded update — it commits the rival's
    /// state first, simulating a competing write that landed in between.
    /// </summary>
    private sealed class RivalCommittingDb : IBeaconDb
    {
        private readonly IBeaconDb _inner;
        private readonly Action _rivalCommit;
        private bool _rivalCommitted;

        private RivalCommittingDb(IBeaconDb inner, Action rivalCommit)
        {
            _inner = inner;
            _rivalCommit = rivalCommit;
        }

        public static IBeaconDb Wrap(IBeaconDb inner, Action rivalCommit) => new RivalCommittingDb(inner, rivalCommit);

        public DatabaseFacade Database
        {
            get
            {
                if (!_rivalCommitted)
                {
                    _rivalCommitted = true;
                    _rivalCommit();
                }
                return _inner.Database;
            }
        }

        public Guid? FilterProjectId => _inner.FilterProjectId;
        public Guid? FilterOrgId => _inner.FilterOrgId;
        public bool FilterUnscoped => _inner.FilterUnscoped;

        public DbSet<User> Users => _inner.Users;
        public DbSet<OpenCodeConnection> OpenCodeConnections => _inner.OpenCodeConnections;
        public DbSet<UserSession> Sessions => _inner.Sessions;
        public DbSet<Org> Orgs => _inner.Orgs;
        public DbSet<OrgMember> OrgMembers => _inner.OrgMembers;
        public DbSet<OrgInvite> OrgInvites => _inner.OrgInvites;
        public DbSet<PasswordResetToken> PasswordResetTokens => _inner.PasswordResetTokens;
        public DbSet<Project> Projects => _inner.Projects;
        public DbSet<ProjectMember> ProjectMembers => _inner.ProjectMembers;
        public DbSet<ProjectInvite> ProjectInvites => _inner.ProjectInvites;
        public DbSet<ApiToken> ApiTokens => _inner.ApiTokens;
        public DbSet<TaskItem> Tasks => _inner.Tasks;
        public DbSet<Label> Labels => _inner.Labels;
        public DbSet<LabelPath> LabelPaths => _inner.LabelPaths;
        public DbSet<Report> Reports => _inner.Reports;
        public DbSet<Milestone> Milestones => _inner.Milestones;
        public DbSet<TaskDependency> TaskDependencies => _inner.TaskDependencies;
        public DbSet<TaskComment> TaskComments => _inner.TaskComments;
        public DbSet<Constraint> Constraints => _inner.Constraints;
        public DbSet<Decision> Decisions => _inner.Decisions;
        public DbSet<DecisionTask> DecisionTasks => _inner.DecisionTasks;
        public DbSet<LocalModelBackend> LocalModelBackends => _inner.LocalModelBackends;
        public DbSet<AgentTemplate> AgentTemplates => _inner.AgentTemplates;
        public DbSet<TaskKind> TaskKinds => _inner.TaskKinds;
        public DbSet<TaskKindPhase> TaskKindPhases => _inner.TaskKindPhases;
        public DbSet<TaskPhase> TaskPhases => _inner.TaskPhases;
        public DbSet<RoleBinding> RoleBindings => _inner.RoleBindings;
        public DbSet<Subtask> Subtasks => _inner.Subtasks;
        public DbSet<PipelineSession> PipelineSessions => _inner.PipelineSessions;
        public DbSet<ReviewVerdict> ReviewVerdicts => _inner.ReviewVerdicts;
        public DbSet<DaemonDevice> DaemonDevices => _inner.DaemonDevices;
        public DbSet<WorkstationCommand> WorkstationCommands => _inner.WorkstationCommands;
        public DbSet<ProjectRuntime> ProjectRuntimes => _inner.ProjectRuntimes;
        public DbSet<DeviceHostSample> DeviceHostSamples => _inner.DeviceHostSamples;
        public DbSet<TaskStep> TaskSteps => _inner.TaskSteps;
        public DbSet<ChatSession> ChatSessions => _inner.ChatSessions;
        public DbSet<ChatPart> ChatParts => _inner.ChatParts;
        public DbSet<EvalRun> EvalRuns => _inner.EvalRuns;
        public DbSet<ReviewRun> ReviewRuns => _inner.ReviewRuns;
        public DbSet<ContextSection> ContextSections => _inner.ContextSections;
        public DbSet<ContextRevision> ContextRevisions => _inner.ContextRevisions;

        public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class
            => _inner.Entry(entity);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => _inner.SaveChangesAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Forwards every call to the wrapped context. Its <c>SaveChangesAsync</c> throws a
    /// <c>DbUpdateException</c> to simulate the "Work finished" comment insert failing —
    /// in the finish-work flow that single SaveChanges call is the comment insert.
    /// </summary>
    private sealed class CommentSaveFailingDb : IBeaconDb
    {
        private readonly IBeaconDb _inner;

        private CommentSaveFailingDb(IBeaconDb inner) => _inner = inner;

        public static IBeaconDb Wrap(IBeaconDb inner) => new CommentSaveFailingDb(inner);

        public DatabaseFacade Database => _inner.Database;

        public Guid? FilterProjectId => _inner.FilterProjectId;
        public Guid? FilterOrgId => _inner.FilterOrgId;
        public bool FilterUnscoped => _inner.FilterUnscoped;

        public DbSet<User> Users => _inner.Users;
        public DbSet<OpenCodeConnection> OpenCodeConnections => _inner.OpenCodeConnections;
        public DbSet<UserSession> Sessions => _inner.Sessions;
        public DbSet<Org> Orgs => _inner.Orgs;
        public DbSet<OrgMember> OrgMembers => _inner.OrgMembers;
        public DbSet<OrgInvite> OrgInvites => _inner.OrgInvites;
        public DbSet<PasswordResetToken> PasswordResetTokens => _inner.PasswordResetTokens;
        public DbSet<Project> Projects => _inner.Projects;
        public DbSet<ProjectMember> ProjectMembers => _inner.ProjectMembers;
        public DbSet<ProjectInvite> ProjectInvites => _inner.ProjectInvites;
        public DbSet<ApiToken> ApiTokens => _inner.ApiTokens;
        public DbSet<TaskItem> Tasks => _inner.Tasks;
        public DbSet<Label> Labels => _inner.Labels;
        public DbSet<LabelPath> LabelPaths => _inner.LabelPaths;
        public DbSet<Report> Reports => _inner.Reports;
        public DbSet<Milestone> Milestones => _inner.Milestones;
        public DbSet<TaskDependency> TaskDependencies => _inner.TaskDependencies;
        public DbSet<TaskComment> TaskComments => _inner.TaskComments;
        public DbSet<Constraint> Constraints => _inner.Constraints;
        public DbSet<Decision> Decisions => _inner.Decisions;
        public DbSet<DecisionTask> DecisionTasks => _inner.DecisionTasks;
        public DbSet<LocalModelBackend> LocalModelBackends => _inner.LocalModelBackends;
        public DbSet<AgentTemplate> AgentTemplates => _inner.AgentTemplates;
        public DbSet<TaskKind> TaskKinds => _inner.TaskKinds;
        public DbSet<TaskKindPhase> TaskKindPhases => _inner.TaskKindPhases;
        public DbSet<TaskPhase> TaskPhases => _inner.TaskPhases;
        public DbSet<RoleBinding> RoleBindings => _inner.RoleBindings;
        public DbSet<Subtask> Subtasks => _inner.Subtasks;
        public DbSet<PipelineSession> PipelineSessions => _inner.PipelineSessions;
        public DbSet<ReviewVerdict> ReviewVerdicts => _inner.ReviewVerdicts;
        public DbSet<DaemonDevice> DaemonDevices => _inner.DaemonDevices;
        public DbSet<WorkstationCommand> WorkstationCommands => _inner.WorkstationCommands;
        public DbSet<ProjectRuntime> ProjectRuntimes => _inner.ProjectRuntimes;
        public DbSet<DeviceHostSample> DeviceHostSamples => _inner.DeviceHostSamples;
        public DbSet<TaskStep> TaskSteps => _inner.TaskSteps;
        public DbSet<ChatSession> ChatSessions => _inner.ChatSessions;
        public DbSet<ChatPart> ChatParts => _inner.ChatParts;
        public DbSet<EvalRun> EvalRuns => _inner.EvalRuns;
        public DbSet<ReviewRun> ReviewRuns => _inner.ReviewRuns;
        public DbSet<ContextSection> ContextSections => _inner.ContextSections;
        public DbSet<ContextRevision> ContextRevisions => _inner.ContextRevisions;

        public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class
            => _inner.Entry(entity);

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => Task.FromException<int>(new DbUpdateException("injected comment insert failure"));

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class FixedDbFactory : IBeaconDbFactory
    {
        private readonly IBeaconDb _db;

        public FixedDbFactory(IBeaconDb db) => _db = db;

        public IBeaconDb CreateDbContext() => _db;

        public IBeaconDb CreateDbContext(ITenantContext tenant) => _db;
    }
}
