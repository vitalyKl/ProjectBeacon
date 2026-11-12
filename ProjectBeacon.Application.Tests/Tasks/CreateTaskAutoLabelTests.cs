namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CreateTaskAutoLabelTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;

    public CreateTaskAutoLabelTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("P", null, org.Id);
        var owner = User.Create("owner", "owner@example.test", "hash");
        _db.Users.Add(owner);
        _db.Projects.Add(project);
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
        _db.Labels.Add(Label.Create("API", "#000", project.Id, "ProjectBeacon.API"));
        _db.SaveChanges();
        _projectId = project.Id;
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateTask_NonMember_IsForbidden()
    {
        var outsider = User.Create("outsider", "outsider@example.test", "hash");
        _db.Users.Add(outsider);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(ActorType.Human, outsider.Id, false, null, null, null, null, ApiTokenCapability.None);
        var handler = new CreateTaskHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var result = await handler.HandleAsync(new CreateTaskCommand(new CreateTaskRequest(
            "Forbidden task", null, _projectId, TaskPriority.Medium, TaskType.Task, null, null), actor),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task CreateTask_WithPath_AssignsMatchingLabel()
    {
        var owner = await _db.Users.SingleAsync(user => user.Login == "owner", TestContext.Current.CancellationToken);
        var handler = new CreateTaskHandler(HandlerSqlite.Factory(_connection), new AuthorizationService());
        var actor = new ActorContext(ActorType.Human, owner.Id, false, null, null, null, null, ApiTokenCapability.None);
        var result = await handler.HandleAsync(new CreateTaskCommand(new CreateTaskRequest(
            "Fix API",
            null,
            _projectId,
            TaskPriority.Medium,
            TaskType.Task,
            null,
            null,
            "ProjectBeacon.API/Program.cs"), actor), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        var label = Assert.Single(_db.Labels);
        Assert.Equal(label.Id, result.Value!.LabelId);
    }
}
