namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class ListOrgProjectsTenantScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public ListOrgProjectsTenantScopeTests()
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
    public async Task OrgProjectList_UsesRequestedOrgInsteadOfAmbientOrgScope()
    {
        var member = User.Create("multi-org", "multi-org@example.test", "hash");
        var outsider = User.Create("outsider", "outsider@example.test", "hash");
        var activeOrg = Org.Create("Active org");
        var requestedOrg = Org.Create("Requested org");
        var activeProject = Project.Create("Active project", null, activeOrg.Id);
        var requestedProject = Project.Create("Requested project", null, requestedOrg.Id);

        using (TenantScope.EnterUnscoped())
        {
            _db.Users.AddRange(member, outsider);
            _db.Orgs.AddRange(activeOrg, requestedOrg);
            _db.OrgMembers.AddRange(
                OrgMember.Create(activeOrg.Id, member.Id, MemberRole.Owner),
                OrgMember.Create(requestedOrg.Id, member.Id, MemberRole.Owner));
            _db.Projects.AddRange(activeProject, requestedProject);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var handler = new ListProjectsHandler(HandlerSqlite.Factory(_connection));
        var actor = new ActorContext(
            ActorType.Human, member.Id, false, null, null, null, null, ApiTokenCapability.None);

        using (TenantScope.EnterOrgScope(activeOrg.Id))
        {
            var visible = await handler.HandleAsync(
                new ListProjectsCommand(new ListProjectsRequest(requestedOrg.Id), actor),
                TestContext.Current.CancellationToken);

            Assert.True(visible.Success, visible.Error);
            var projects = Assert.IsAssignableFrom<IList<ProjectDto>>(visible.Value);
            var project = Assert.Single(projects);
            Assert.Equal(requestedProject.Id, project.Id);
            Assert.Equal(requestedOrg.Id, project.OrgId);

            var denied = await handler.HandleAsync(
                new ListProjectsCommand(
                    new ListProjectsRequest(requestedOrg.Id),
                    new ActorContext(ActorType.Human, outsider.Id, false, null, null, null, null, ApiTokenCapability.None)),
                TestContext.Current.CancellationToken);
            Assert.False(denied.Success);
            Assert.Equal(ErrorKind.NotFound, denied.Kind);
        }
    }
}
