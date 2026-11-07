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

public sealed class TaskWorkspaceHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public TaskWorkspaceHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Workspace_CreatesAppendsIndexesAndReadsSections()
    {
        var (projectId, task) = await SeedTaskAsync();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var actor = ApiActor(Guid.NewGuid());
        var writer = new WorkspaceWriteHandler(factory);

        var created = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "plan/overview", "Plan", "Current implementation plan",
                "1. Inspect the code.", "create"), actor, "Planner"));
        Assert.True(created.Success, created.Error);
        Assert.Equal(1, created.Value!.Revision);
        Assert.Equal("Planner", created.Value.AuthorRole);

        var appended = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "plan/overview", null, null,
                "2. Implement the changes.", "append"), actor, "Planner"));
        Assert.True(appended.Success, appended.Error);
        Assert.Equal(2, appended.Value!.Revision);
        Assert.Equal("1. Inspect the code.\n\n2. Implement the changes.", appended.Value.Content);

        var index = await new WorkspaceIndexHandler(factory).HandleAsync(new WorkspaceIndexQuery(
            new WorkspaceIndexRequest(task.Id, Query: "implementation", Limit: 10)));
        Assert.True(index.Success, index.Error);
        var section = Assert.Single(index.Value!);
        Assert.Equal("plan/overview", section.Key);
        Assert.Equal(2, section.Revision);

        var read = await new WorkspaceReadHandler(factory).HandleAsync(new WorkspaceReadQuery(
            new WorkspaceReadRequest(task.Id, "plan/overview", Offset: 5, Limit: 12)));
        Assert.True(read.Success, read.Error);
        Assert.Equal("Inspect the ", read.Value!.Content);
    }

    [Fact]
    public async Task Workspace_ReplaceRequiresAuthorAndExpectedRevision()
    {
        var (projectId, task) = await SeedTaskAsync();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var author = ApiActor(Guid.NewGuid());
        var other = ApiActor(Guid.NewGuid());
        var writer = new WorkspaceWriteHandler(factory);

        var created = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "notes/decisions", "Decisions", "Shared technical decisions",
                "Keep the API backwards compatible.", "create"), author, "Actor"));
        Assert.True(created.Success, created.Error);

        var denied = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "notes/decisions", "Decisions", "Updated",
                "Overwrite attempt.", "replace", ExpectedRevision: 1), other, "Actor"));
        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);

        var stale = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "notes/decisions", "Decisions", "Updated",
                "Stale attempt.", "replace", ExpectedRevision: 2), author, "Actor"));
        Assert.False(stale.Success);
        Assert.Equal(ErrorKind.Conflict, stale.Kind);

        var replaced = await writer.HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, "notes/decisions", "Decisions", "Updated",
                "Revised decision.", "replace", ExpectedRevision: 1), author, "Actor"));
        Assert.True(replaced.Success, replaced.Error);
        Assert.Equal(2, replaced.Value!.Revision);
        Assert.Equal("Revised decision.", replaced.Value.Content);
    }

    [Fact]
    public async Task Workspace_ServerGeneratedReportsAreImmutable()
    {
        var (projectId, task) = await SeedTaskAsync();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var section = TaskWorkspaceSection.Create(
            task.Id, projectId, "report/subtask-a", "Subtask report", "Verified work",
            "Report", "The actor completed the assigned change.", "Server", null,
            "Actor", null, Guid.NewGuid(), Guid.NewGuid());
        using (TenantScope.EnterUnscoped())
        {
            _db.TaskWorkspaceSections.Add(section);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await new WorkspaceWriteHandler(factory).HandleAsync(new WorkspaceWriteCommand(
            new WorkspaceWriteRequest(task.Id, section.Key, null, null, "Injected content", "append"),
            ApiActor(Guid.NewGuid()), "Actor"));

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    private async Task<(Guid ProjectId, TaskItem Task)> SeedTaskAsync()
    {
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create("WorkspaceOrg", null);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("WorkspaceProject", null, org.Id);
            _db.Projects.Add(project);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var task = TaskItem.Create("Workspace task", project.Id);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return (project.Id, task);
        }
    }

    private static TenantContext Scope(Guid projectId)
    {
        var tenant = new TenantContext();
        tenant.Assign(projectId, null, unscoped: false);
        return tenant;
    }

    private static ActorContext ApiActor(Guid tokenId) =>
        new(ActorType.ApiToken, null, false, tokenId, null, null, null,
            ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite);
}
