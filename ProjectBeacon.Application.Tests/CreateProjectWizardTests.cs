namespace ProjectBeacon.Application.Tests;

using Application.Identity;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class CreateProjectWizardTests : IDisposable
{
    private readonly BeaconDbContext _db;
    private readonly SqliteConnection _connection;
    private readonly IDisposable _unscoped;

    public CreateProjectWizardTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Wizard_CreatesOrgProjectAndStarterLabels()
    {
        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection)).HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Acme", null)));
        Assert.True(org.Success, org.Error);

        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Beacon", "desc", org.Value!.Id)));
        Assert.True(project.Success, project.Error);
        Assert.Equal(org.Value.Id, project.Value!.OrgId);

        var labels = await new ListLabelsHandler(HandlerSqlite.Factory(_connection)).HandleAsync(new ListLabelsRequest(project.Value.Id));
        Assert.True(labels.Success);
        Assert.Equal(5, labels.Value!.Count);
        Assert.Contains(labels.Value, l => l.Name == "API" && l.PathPrefix == "ProjectBeacon.API");
        Assert.Contains(labels.Value, l => l.Name == "Web");
        Assert.Contains(labels.Value, l => l.Name == "CLI");
    }

    [Fact]
    public async Task Wizard_AddsCreatorAsOwner_AndCurrentProjectIgnoresStaleTenantScope()
    {
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection)).HandleAsync(
            new CreateOrgCommand(new CreateOrgRequest("Acme", null, user.Id)));
        Assert.True(org.Success, org.Error);

        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Beacon", "desc", org.Value!.Id, user.Id)));
        Assert.True(project.Success, project.Error);

        var orgMember = await _db.OrgMembers.SingleAsync(m => m.OrgId == org.Value.Id);
        Assert.Equal(user.Id, orgMember.UserId);
        Assert.Equal(MemberRole.Owner, orgMember.Role);

        var projectMember = await _db.ProjectMembers.SingleAsync(m => m.ProjectId == project.Value!.Id);
        Assert.Equal(user.Id, projectMember.UserId);
        Assert.Equal(MemberRole.Owner, projectMember.Role);

        using (TenantScope.EnterProjectScope(Guid.Empty))
        using (TenantScope.EnterOrgScope(Guid.Empty))
        {
            var missed = await new GetCurrentProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync();
            Assert.Null(missed.Value);

            var current = await new GetCurrentProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(user.Id, isAdmin: false);
            Assert.True(current.Success);
            Assert.NotNull(current.Value);
            Assert.Equal(project.Value.Id, current.Value.Id);
        }
    }
}
