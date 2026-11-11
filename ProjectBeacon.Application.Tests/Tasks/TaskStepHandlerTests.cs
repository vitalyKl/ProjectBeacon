namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Authorization;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class TaskStepHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public TaskStepHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory(ITenantContext? tenant = null) =>
        HandlerSqlite.Factory(_connection, tenant);

    [Fact]
    public async Task Add_Toggle_Delete_RoundTrip()
    {
        Guid taskId;
        Guid projectId;
        Guid userId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var task = TaskItem.Create("Work", project.Id);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            projectId = project.Id;
            userId = user.Id;
        }

        var actor = new ActorContext(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);
        var authorization = new AuthorizationService();
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = Factory(tenant);

        var added = await new AddTaskStepHandler(factory, authorization).HandleAsync(
            new AddTaskStepCommand(new AddTaskStepRequest(taskId, "Write tests"), actor), TestContext.Current.CancellationToken);
        Assert.True(added.Success, added.Error);
        Assert.False(added.Value!.IsDone);

        var listed = await new ListTaskStepsHandler(factory, authorization).HandleAsync(
            new ListTaskStepsCommand(new ListTaskStepsRequest(taskId), actor), TestContext.Current.CancellationToken);
        Assert.Single(listed.Value!);

        var toggled = await new ToggleTaskStepHandler(factory, authorization).HandleAsync(
            new ToggleTaskStepCommand(new ToggleTaskStepRequest(added.Value.Id, true), actor), TestContext.Current.CancellationToken);
        Assert.True(toggled.Value!.IsDone);

        var deleted = await new DeleteTaskStepHandler(factory, authorization).HandleAsync(
            new DeleteTaskStepCommand(new DeleteTaskStepRequest(added.Value.Id), actor), TestContext.Current.CancellationToken);
        Assert.True(deleted.Success);
        var empty = await new ListTaskStepsHandler(factory, authorization).HandleAsync(
            new ListTaskStepsCommand(new ListTaskStepsRequest(taskId), actor), TestContext.Current.CancellationToken);
        Assert.Empty(empty.Value!);
    }

    [Fact]
    public async Task Add_RejectsBlankTitle()
    {
        var result = await new AddTaskStepHandler(Factory(), new AuthorizationService()).HandleAsync(
            new AddTaskStepCommand(new AddTaskStepRequest(Guid.NewGuid(), "  "), ActorContext.Anonymous), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
    }
}
