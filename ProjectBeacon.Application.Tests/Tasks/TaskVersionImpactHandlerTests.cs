namespace ProjectBeacon.Application.Tests.Tasks;

using Application.Authorization;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Audit;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class TaskVersionImpactHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly Guid _ownerId;
    private readonly Guid _memberId;
    private readonly Guid _taskId;

    public TaskVersionImpactHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();

        var org = Org.Create("Task Version Impact Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();

        var project = Project.Create("Task Version Impact Project", null, org.Id);
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;

        var owner = User.Create("impact-owner", "impact-owner@beacon.local", "hash");
        var member = User.Create("impact-member", "impact-member@beacon.local", "hash");
        _db.Users.AddRange(owner, member);
        _db.SaveChanges();
        _ownerId = owner.Id;
        _memberId = member.Id;

        _db.ProjectMembers.AddRange(
            ProjectMember.Create(_projectId, _ownerId, MemberRole.Owner),
            ProjectMember.Create(_projectId, _memberId, MemberRole.Member));
        var task = TaskItem.Create("Version impact task", _projectId);
        _db.Tasks.Add(task);
        _db.SaveChanges();
        _taskId = task.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private ActorContext HumanActor(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, _projectId, null, ApiTokenCapability.None);

    private ActorContext ProjectToken(ApiTokenCapability capabilities, Guid? projectId = null) =>
        new(ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId ?? _projectId, null, capabilities);

    private SetTaskVersionImpactHandler Setter() =>
        new(HandlerSqlite.Factory(_connection), new EfAuditWriter());

    [Fact]
    public async Task ProjectMemberCanDeclareImpact_AndRevisionPreventsStaleWrites()
    {
        var setter = Setter();
        var saved = await setter.HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "minor", "Adds a user-facing capability.", 1, HumanActor(_memberId)),
            TestContext.Current.CancellationToken);

        Assert.True(saved.Success, saved.Error);
        Assert.Equal("minor", saved.Value!.Impact);
        Assert.Equal(2, saved.Value.Revision);

        var stale = await setter.HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "patch", "Stale update.", 1, HumanActor(_memberId)),
            TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);

        Assert.Contains(await _db.AuditEvents.Select(item => item.Action)
            .ToListAsync(TestContext.Current.CancellationToken), action => action == "task.version_impact.declared");
    }

    [Fact]
    public async Task ApiTokenMustHaveExactProjectScopeAndTaskWriteCapability()
    {
        var setter = Setter();
        var wrongProject = await setter.HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "patch", "Wrong project token.", 1,
            ProjectToken(ApiTokenCapability.TaskWrite, Guid.NewGuid())),
            TestContext.Current.CancellationToken);
        Assert.False(wrongProject.Success);
        Assert.Equal(ErrorKind.Forbidden, wrongProject.Kind);

        var readOnly = await setter.HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "patch", "Read only token.", 1,
            ProjectToken(ApiTokenCapability.TaskRead)),
            TestContext.Current.CancellationToken);
        Assert.False(readOnly.Success);
        Assert.Equal(ErrorKind.Forbidden, readOnly.Kind);

        var allowed = await setter.HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "patch", "Correct project token.", 1,
            ProjectToken(ApiTokenCapability.TaskWrite)),
            TestContext.Current.CancellationToken);
        Assert.True(allowed.Success, allowed.Error);
    }

    [Fact]
    public async Task OnlyActiveReviewerSessionCanConfirmImpact()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var token = ProjectToken(ApiTokenCapability.TaskWrite);
        var declared = await Setter().HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "major", "Breaking change.", 1, token),
            TestContext.Current.CancellationToken);
        Assert.True(declared.Success, declared.Error);

        var session = PipelineSession.Create(_taskId, _projectId, PipelineRole.Review, "Review the declared version impact.");
        session.Launch();
        _db.PipelineSessions.Add(session);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reviewer = new ReviewTaskVersionImpactHandler(factory, new EfAuditWriter());
        var actorRoleAttempt = await reviewer.HandleAsync(new ReviewTaskVersionImpactCommand(
            _taskId, "Confirmed", "Should not be accepted from Actor.", declared.Value!.Revision,
            session.Id, PipelineRole.Actor, token),
            TestContext.Current.CancellationToken);
        Assert.False(actorRoleAttempt.Success);
        Assert.Equal(ErrorKind.Forbidden, actorRoleAttempt.Kind);

        var confirmed = await reviewer.HandleAsync(new ReviewTaskVersionImpactCommand(
            _taskId, "Confirmed", "The declared major impact matches the change.", declared.Value.Revision,
            session.Id, PipelineRole.Review, token),
            TestContext.Current.CancellationToken);
        Assert.True(confirmed.Success, confirmed.Error);
        Assert.Equal("Confirmed", confirmed.Value!.ReviewStatus);
        Assert.Equal(session.Id, confirmed.Value.ReviewedBySessionId);
        Assert.Equal(declared.Value.Revision + 1, confirmed.Value.Revision);

        var stale = await reviewer.HandleAsync(new ReviewTaskVersionImpactCommand(
            _taskId, "Challenged", "Stale review.", declared.Value.Revision,
            session.Id, PipelineRole.Review, token),
            TestContext.Current.CancellationToken);
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);
    }

    [Fact]
    public async Task ReviewCannotUseClosedOrWrongTaskSession()
    {
        var factory = HandlerSqlite.Factory(_connection);
        var token = ProjectToken(ApiTokenCapability.TaskWrite);
        var declared = await Setter().HandleAsync(new SetTaskVersionImpactCommand(
            _taskId, "patch", "Small fix.", 1, token),
            TestContext.Current.CancellationToken);
        Assert.True(declared.Success, declared.Error);

        var wrongTask = TaskItem.Create("Other task", _projectId);
        _db.Tasks.Add(wrongTask);
        var closed = PipelineSession.Create(wrongTask.Id, _projectId, PipelineRole.Review, "Closed unrelated review.");
        closed.Launch();
        closed.Close();
        _db.PipelineSessions.Add(closed);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new ReviewTaskVersionImpactHandler(factory, new EfAuditWriter())
            .HandleAsync(new ReviewTaskVersionImpactCommand(
                _taskId, "Confirmed", "Should fail session binding.", declared.Value!.Revision,
                closed.Id, PipelineRole.Review, token),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }
}
