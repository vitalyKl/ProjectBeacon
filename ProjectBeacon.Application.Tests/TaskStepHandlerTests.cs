namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
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

    private IDbContextFactory<BeaconDbContext> Factory(ITenantContext? tenant = null) =>
        HandlerSqlite.Factory(_connection, tenant);

    [Fact]
    public async Task Add_Toggle_Delete_RoundTrip()
    {
        Guid taskId;
        Guid projectId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var task = TaskItem.Create("Work", project.Id);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync();
            taskId = task.Id;
            projectId = project.Id;
        }

        var tenant = new TenantContext();
        tenant.Assign(projectId, null, false);
        var factory = Factory(tenant);

        var added = await new AddTaskStepHandler(factory).HandleAsync(
            new AddTaskStepCommand(new AddTaskStepRequest(taskId, "Write tests")));
        Assert.True(added.Success, added.Error);
        Assert.False(added.Value!.IsDone);

        var listed = await new ListTaskStepsHandler(factory).HandleAsync(
            new ListTaskStepsCommand(new ListTaskStepsRequest(taskId)));
        Assert.Single(listed.Value!);

        var toggled = await new ToggleTaskStepHandler(factory).HandleAsync(
            new ToggleTaskStepCommand(new ToggleTaskStepRequest(added.Value.Id, true)));
        Assert.True(toggled.Value!.IsDone);

        var deleted = await new DeleteTaskStepHandler(factory).HandleAsync(
            new DeleteTaskStepCommand(new DeleteTaskStepRequest(added.Value.Id)));
        Assert.True(deleted.Success);
        var empty = await new ListTaskStepsHandler(factory).HandleAsync(
            new ListTaskStepsCommand(new ListTaskStepsRequest(taskId)));
        Assert.Empty(empty.Value!);
    }

    [Fact]
    public async Task Add_RejectsBlankTitle()
    {
        var result = await new AddTaskStepHandler(Factory()).HandleAsync(
            new AddTaskStepCommand(new AddTaskStepRequest(Guid.NewGuid(), "  ")));
        Assert.False(result.Success);
    }
}
