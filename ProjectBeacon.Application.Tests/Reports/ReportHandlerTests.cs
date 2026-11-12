namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Reports;
using Application.Authorization;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ReportHandlerTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;
    private readonly Guid _projectId;
    private readonly ActorContext _actor;

    public ReportHandlerTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        var user = User.Create("report-reader", "report-reader@beacon.local", "hash");
        var org = Org.Create("Org");
        _db.Users.Add(user);
        _db.Orgs.Add(org);
        _db.SaveChanges();
        var project = Project.Create("Beacon", null, org.Id);
        _db.Projects.Add(project);
        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Member));
        var todo = TaskItem.Create("Ready work", project.Id);
        var doing = TaskItem.Create("In flight", project.Id);
        doing.MoveToNextStatus();
        _db.Tasks.AddRange(todo, doing);
        _db.Milestones.Add(Milestone.Create("M1", null, project.Id, 0));
        _db.SaveChanges();
        _projectId = project.Id;
        _actor = new ActorContext(
            ActorType.Human, user.Id, false, null, null,
            project.Id, org.Id, ApiTokenCapability.None);
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ReportHandlers_RejectActorWithoutProjectMembership()
    {
        var generated = await new GenerateReportHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(
            new GenerateReportCommand(new GenerateReportRequest(_projectId), _actor),
            TestContext.Current.CancellationToken);
        Assert.True(generated.Success, generated.Error);

        var outsider = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null,
            _actor.ProjectId, _actor.OrgId, ApiTokenCapability.None);

        var list = await new ListReportsHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(
            new ListReportsRequest(_projectId, outsider), TestContext.Current.CancellationToken);
        Assert.False(list.Success);
        Assert.Equal(ErrorKind.Forbidden, list.Kind);

        var get = await new GetReportHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(
            new GetReportRequest(_projectId, generated.Value!.Id, outsider),
            TestContext.Current.CancellationToken);
        Assert.False(get.Success);
        Assert.Equal(ErrorKind.Forbidden, get.Kind);
    }

    [Fact]
    public async Task Generate_ThenList_ReturnsSnapshot()
    {
        var generated = await new GenerateReportHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(
            new GenerateReportCommand(new GenerateReportRequest(_projectId), _actor),
            TestContext.Current.CancellationToken);
        Assert.True(generated.Success, generated.Error);
        Assert.Contains("Ready work", generated.Value!.BodyMarkdown);
        Assert.Contains("In flight", generated.Value.BodyMarkdown);
        Assert.Contains("\"todo\":1", generated.Value.SnapshotJson, StringComparison.OrdinalIgnoreCase);

        var listed = await new ListReportsHandler(
            HandlerSqlite.Factory(_connection), new AuthorizationService()).HandleAsync(
            new ListReportsRequest(_projectId, _actor), TestContext.Current.CancellationToken);
        Assert.True(listed.Success);
        Assert.Single(listed.Value!);
        Assert.Equal(generated.Value!.Id, listed.Value![0].Id);
    }
}
