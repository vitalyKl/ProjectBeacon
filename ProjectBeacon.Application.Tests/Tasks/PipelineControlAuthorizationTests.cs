namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class PipelineControlAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly AuthorizationService _authorization = new();
    private readonly Guid _projectId;
    private readonly Guid _taskId;
    private readonly Guid _memberId;
    private readonly Guid _otherProjectId;

    public PipelineControlAuthorizationTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);

        var org = Org.Create("Pipeline control authorization org");
        var project = Project.Create("Pipeline control project", null, org.Id);
        var otherProject = Project.Create("Other pipeline project", null, org.Id);
        var member = User.Create("pipeline-member", "pipeline-member@example.test", "hash");
        var task = TaskItem.Create("Pipeline task", project.Id);

        _db.Orgs.Add(org);
        _db.Users.Add(member);
        _db.Projects.AddRange(project, otherProject);
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, member.Id, MemberRole.Member));
        _db.Tasks.Add(task);
        _db.SaveChanges();

        _projectId = project.Id;
        _taskId = task.Id;
        _memberId = member.Id;
        _otherProjectId = otherProject.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task NonMemberCannotCancelOrRestartPipeline()
    {
        var stranger = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, null, null, ApiTokenCapability.None);

        var cancel = await new CancelPipelineHandler(_factory, _authorization).HandleAsync(
            new CancelPipelineCommand(new CancelPipelineRequest(_taskId), stranger),
            TestContext.Current.CancellationToken);
        var restart = await new RestartPipelineHandler(_factory, _authorization).HandleAsync(
            new RestartPipelineCommand(new RestartPipelineRequest(_taskId), stranger),
            TestContext.Current.CancellationToken);

        Assert.False(cancel.Success);
        Assert.False(restart.Success);
        Assert.Equal(TaskPipelineStage.None, (await _db.Tasks.AsNoTracking()
            .SingleAsync(task => task.Id == _taskId, TestContext.Current.CancellationToken)).PipelineStage
            ?? TaskPipelineStage.None);
    }

    [Fact]
    public async Task ProjectMemberPassesAuthorizationBeforePipelineStateValidation()
    {
        var member = new ActorContext(
            ActorType.Human, _memberId, false, null, null, null, null, ApiTokenCapability.None);

        var cancel = await new CancelPipelineHandler(_factory, _authorization).HandleAsync(
            new CancelPipelineCommand(new CancelPipelineRequest(_taskId), member),
            TestContext.Current.CancellationToken);
        var restart = await new RestartPipelineHandler(_factory, _authorization).HandleAsync(
            new RestartPipelineCommand(new RestartPipelineRequest(_taskId), member),
            TestContext.Current.CancellationToken);

        Assert.False(cancel.Success);
        Assert.Contains("Pipeline is not started", cancel.Error);
        Assert.False(restart.Success);
        Assert.Contains("Pipeline has not been initialized", restart.Error);
    }

    [Fact]
    public async Task NonOwnerCannotApprovePipeline()
    {
        var member = new ActorContext(
            ActorType.Human, _memberId, false, null, null, null, null, ApiTokenCapability.None);

        var result = await new ApprovePipelineHandler(_factory, _authorization).HandleAsync(
            new ApprovePipelineCommand(new ApprovePipelineRequest(_taskId), member),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("forbidden", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonMemberCannotProbeOrCancelPipelineSession()
    {
        var session = PipelineSession.Create(
            _taskId, _projectId, PipelineRole.Planner,
            "private planner context", null, null, null);
        _db.PipelineSessions.Add(session);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stranger = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, null, null, ApiTokenCapability.None);
        var result = await new CancelPipelineSessionHandler(_factory, _authorization).HandleAsync(
            new CancelPipelineSessionCommand(
                new CancelPipelineSessionRequest(_taskId, session.Id), stranger),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        var persisted = await _db.PipelineSessions.AsNoTracking()
            .SingleAsync(item => item.Id == session.Id, TestContext.Current.CancellationToken);
        Assert.Equal(SessionStatus.Ready, persisted.Status);
    }

    [Fact]
    public async Task SessionDriveTokenMustBeBoundToTheTaskProject()
    {
        var token = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _otherProjectId, null, ApiTokenCapability.SessionDrive);

        var cancel = await new CancelPipelineHandler(_factory, _authorization).HandleAsync(
            new CancelPipelineCommand(new CancelPipelineRequest(_taskId), token),
            TestContext.Current.CancellationToken);
        var restart = await new RestartPipelineHandler(_factory, _authorization).HandleAsync(
            new RestartPipelineCommand(new RestartPipelineRequest(_taskId), token),
            TestContext.Current.CancellationToken);

        Assert.False(cancel.Success);
        Assert.False(restart.Success);
    }

    [Fact]
    public async Task CreateSubtaskRequiresProjectExecutePermission()
    {
        var stranger = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, null, null, ApiTokenCapability.None);
        var wrongProjectToken = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _otherProjectId, null, ApiTokenCapability.SessionDrive);

        var strangerResult = await new CreateSubtaskHandler(_factory, _authorization).HandleAsync(
            new CreateSubtaskCommand(
                new CreateSubtaskRequest(_taskId, "stranger work"), stranger),
            TestContext.Current.CancellationToken);
        var tokenResult = await new CreateSubtaskHandler(_factory, _authorization).HandleAsync(
            new CreateSubtaskCommand(
                new CreateSubtaskRequest(_taskId, "wrong project work"), wrongProjectToken),
            TestContext.Current.CancellationToken);

        Assert.False(strangerResult.Success);
        Assert.Equal(ErrorKind.Forbidden, strangerResult.Kind);
        Assert.False(tokenResult.Success);
        Assert.Equal(ErrorKind.Forbidden, tokenResult.Kind);
    }

    [Fact]
    public async Task CreateSubtaskHarnessTokenRequiresAnActivePlannerSession()
    {
        var task = await _db.Tasks.SingleAsync(item => item.Id == _taskId, TestContext.Current.CancellationToken);
        task.StartPipeline();
        var session = PipelineSession.Create(
            _taskId, _projectId, PipelineRole.Planner, "planner context", null, null, null);
        session.Launch("planner-session");
        _db.PipelineSessions.Add(session);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actorId = Guid.NewGuid();
        var wrongRole = new ActorContext(
            ActorType.ApiToken, actorId, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.SessionDrive,
            PipelineRole: PipelineRole.Actor,
            PipelineTaskId: _taskId,
            PipelineSessionId: session.Id);
        var wrongRoleResult = await new CreateSubtaskHandler(_factory, _authorization).HandleAsync(
            new CreateSubtaskCommand(new CreateSubtaskRequest(_taskId, "wrong role work"), wrongRole),
            TestContext.Current.CancellationToken);

        var planner = new ActorContext(
            ActorType.ApiToken, actorId, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.SessionDrive,
            PipelineRole: PipelineRole.Planner,
            PipelineTaskId: _taskId,
            PipelineSessionId: session.Id);
        var plannerResult = await new CreateSubtaskHandler(_factory, _authorization).HandleAsync(
            new CreateSubtaskCommand(new CreateSubtaskRequest(_taskId, "planner work"), planner),
            TestContext.Current.CancellationToken);

        Assert.False(wrongRoleResult.Success);
        Assert.Equal(ErrorKind.Forbidden, wrongRoleResult.Kind);
        Assert.True(plannerResult.Success, plannerResult.Error);
    }

    [Fact]
    public async Task SessionDriveTokenBoundToTaskProjectPassesAuthorization()
    {
        var token = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            _projectId, null, ApiTokenCapability.SessionDrive);

        var cancel = await new CancelPipelineHandler(_factory, _authorization).HandleAsync(
            new CancelPipelineCommand(new CancelPipelineRequest(_taskId), token),
            TestContext.Current.CancellationToken);

        Assert.False(cancel.Success);
        Assert.Contains("Pipeline is not started", cancel.Error);
    }
}
