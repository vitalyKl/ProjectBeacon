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

public sealed class TaskMutationAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly AuthorizationService _authorization = new();
    private readonly Guid _projectId;
    private readonly Guid _memberId;
    private readonly Guid _taskId;
    private readonly Guid _dependentTaskId;
    private readonly Guid _otherProjectId;

    public TaskMutationAuthorizationTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);

        var org = Org.Create("Mutation authorization org");
        var project = Project.Create("Mutation authorization project", null, org.Id);
        var otherProject = Project.Create("Other mutation project", null, org.Id);
        var member = User.Create("mutation-member", "mutation-member@example.test", "hash");
        var task = TaskItem.Create("Work", project.Id);
        task.TransitionTo(TaskItemStatus.InProgress);
        var dependent = TaskItem.Create("Prerequisite", project.Id);

        _db.Orgs.Add(org);
        _db.Users.Add(member);
        _db.Projects.AddRange(project, otherProject);
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, member.Id, MemberRole.Member));
        _db.Tasks.AddRange(task, dependent);
        _db.SaveChanges();

        _projectId = project.Id;
        _memberId = member.Id;
        _taskId = task.Id;
        _dependentTaskId = dependent.Id;
        _otherProjectId = otherProject.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task NonMemberCannotMutateTaskStatusSubstageDependenciesOrReviewNotes()
    {
        var stranger = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, null, null, ApiTokenCapability.None);

        var status = await new ChangeTaskStatusHandler(_factory, _authorization).HandleAsync(
            new ChangeTaskStatusCommand(
                new ChangeTaskStatusRequest(_taskId, TaskItemStatus.Todo), stranger),
            TestContext.Current.CancellationToken);
        var substage = await new ChangeSubStageHandler(_factory, _authorization).HandleAsync(
            new ChangeSubStageCommand(
                new ChangeSubStageRequest(_taskId, TaskSubStage.Review), stranger),
            TestContext.Current.CancellationToken);
        var dependencies = await new SetDependenciesHandler(_factory, _authorization).HandleAsync(
            new SetDependenciesCommand(
                new SetDependenciesRequest(_taskId, [_dependentTaskId]), stranger),
            TestContext.Current.CancellationToken);
        var notes = await new AddReviewNotesHandler(_factory, _authorization).HandleAsync(
            new AddReviewNotesCommand(
                new AddReviewNotesRequest(_taskId, "unauthorized note"), stranger),
            TestContext.Current.CancellationToken);

        Assert.False(status.Success);
        Assert.False(substage.Success);
        Assert.False(dependencies.Success);
        Assert.False(notes.Success);

        var task = await _db.Tasks.AsNoTracking().SingleAsync(
            item => item.Id == _taskId, TestContext.Current.CancellationToken);
        Assert.Equal(TaskItemStatus.InProgress, task.Status);
        Assert.Null(task.ReviewNotes);
        Assert.Empty(await _db.TaskDependencies.AsNoTracking()
            .Where(item => item.TaskId == _taskId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProjectMemberCanUseWorkMutationHandlers()
    {
        var actor = new ActorContext(
            ActorType.Human, _memberId, false, null, null, null, null, ApiTokenCapability.None);

        var substage = await new ChangeSubStageHandler(_factory, _authorization).HandleAsync(
            new ChangeSubStageCommand(
                new ChangeSubStageRequest(_taskId, TaskSubStage.Review), actor),
            TestContext.Current.CancellationToken);
        var dependencies = await new SetDependenciesHandler(_factory, _authorization).HandleAsync(
            new SetDependenciesCommand(
                new SetDependenciesRequest(_taskId, [_dependentTaskId]), actor),
            TestContext.Current.CancellationToken);
        var notes = await new AddReviewNotesHandler(_factory, _authorization).HandleAsync(
            new AddReviewNotesCommand(
                new AddReviewNotesRequest(_taskId, "reviewed"), actor),
            TestContext.Current.CancellationToken);

        Assert.True(substage.Success, substage.Error);
        Assert.True(dependencies.Success, dependencies.Error);
        Assert.True(notes.Success, notes.Error);
        Assert.Equal("reviewed", notes.Value!.ReviewNotes);
        Assert.Equal(new[] { _dependentTaskId }, dependencies.Value!.Dependencies);
    }

    [Fact]
    public async Task TaskWriteTokenBoundToAnotherProjectCannotMutateTask()
    {
        var token = new ActorContext(
            ActorType.ApiToken,
            null,
            false,
            Guid.NewGuid(),
            null,
            _otherProjectId,
            null,
            ApiTokenCapability.TaskWrite);

        var status = await new ChangeTaskStatusHandler(_factory, _authorization).HandleAsync(
            new ChangeTaskStatusCommand(
                new ChangeTaskStatusRequest(_taskId, TaskItemStatus.Todo), token),
            TestContext.Current.CancellationToken);
        var notes = await new AddReviewNotesHandler(_factory, _authorization).HandleAsync(
            new AddReviewNotesCommand(
                new AddReviewNotesRequest(_taskId, "forged project"), token),
            TestContext.Current.CancellationToken);

        Assert.False(status.Success);
        Assert.False(notes.Success);
    }
}
