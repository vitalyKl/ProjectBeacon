namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Application.Identity;
using Application.Persistence;
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
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        var tenant = new TenantContext();
        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection), tenant).HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Acme", null), actor), TestContext.Current.CancellationToken);
        Assert.True(org.Success, org.Error);

        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection), auth: null, tenant).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Beacon", "desc", org.Value!.Id), actor), TestContext.Current.CancellationToken);
        Assert.True(project.Success, project.Error);
        Assert.Equal(org.Value.Id, project.Value!.OrgId);

        var labels = await new ListLabelsHandler(HandlerSqlite.Factory(_connection)).HandleAsync(new ListLabelsRequest(project.Value.Id), TestContext.Current.CancellationToken);
        Assert.True(labels.Success);
        Assert.Equal(5, labels.Value!.Count);
        Assert.Contains(labels.Value, l => l.Name == "API" && l.PathPrefix == "ProjectBeacon.API");
        Assert.Contains(labels.Value, l => l.Name == "Web");
        Assert.Contains(labels.Value, l => l.Name == "CLI");
    }

    [Fact]
    public async Task Create_UsesSeparateTenantInstances_ScopedTenantUntouched()
    {
        var user = User.Create("dave", "dave@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        var scoped = new TenantContext();
        var factory = new CapturingFactory(HandlerSqlite.Factory(_connection));

        var org = await new CreateOrgHandler(factory, scoped).HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Acme", null), actor), TestContext.Current.CancellationToken);
        Assert.True(org.Success, org.Error);
        var project = await new CreateProjectHandler(factory, auth: null, scoped).HandleAsync(new CreateProjectCommand(new CreateProjectRequest("Beacon", null, org.Value!.Id), actor), TestContext.Current.CancellationToken);
        Assert.True(project.Success, project.Error);

        // Every tenant-keyed context ran on a separate instance, never the circuit's
        // scoped tenant — concurrent operations on the same circuit keep their own tenant.
        Assert.NotEmpty(factory.WindowTenants);
        Assert.All(factory.WindowTenants, t => Assert.NotSame(t, scoped));
        Assert.Null(scoped.ProjectId);
        Assert.Null(scoped.OrgId);
        Assert.False(scoped.Unscoped);
    }

    private sealed class CapturingFactory : IBeaconDbFactory
    {
        private readonly IBeaconDbFactory _inner;
        public List<ITenantContext> WindowTenants { get; } = new();

        public CapturingFactory(IBeaconDbFactory inner) => _inner = inner;

        public IBeaconDb CreateDbContext() => _inner.CreateDbContext();

        public IBeaconDb CreateDbContext(ITenantContext tenant)
        {
            WindowTenants.Add(tenant);
            return _inner.CreateDbContext(tenant);
        }
    }

    [Fact]
    public async Task CreateProject_ForeignOrg_Forbidden_WithDescriptiveMessage()
    {
        var owner = User.Create("owner", "owner@example.com", "hash");
        var outsider = User.Create("outsider", "outsider@example.com", "hash");
        _db.Users.Add(owner);
        _db.Users.Add(outsider);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ownerActor = new ActorContext(ActorType.Human, owner.Id, false, null, null, null, null, ApiTokenCapability.None);
        var tenant = new TenantContext();
        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection), tenant).HandleAsync(new CreateOrgCommand(new CreateOrgRequest("Acme", null), ownerActor), TestContext.Current.CancellationToken);
        Assert.True(org.Success, org.Error);

        var outsiderActor = new ActorContext(ActorType.Human, outsider.Id, false, null, null, null, null, ApiTokenCapability.None);
        var result = await new CreateProjectHandler(HandlerSqlite.Factory(_connection), auth: null, tenant).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Intruder", null, org.Value!.Id), outsiderActor), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Equal("You need Owner or Admin rights in this org to create a project.", result.Error);
        Assert.Null(tenant.ProjectId);
    }

    [Fact]
    public async Task Wizard_AddsCreatorAsOwner_AndCurrentProjectIgnoresStaleTenantScope()
    {
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
        var tenant = new TenantContext();
        var org = await new CreateOrgHandler(HandlerSqlite.Factory(_connection), tenant).HandleAsync(
            new CreateOrgCommand(new CreateOrgRequest("Acme", null), actor), TestContext.Current.CancellationToken);
        Assert.True(org.Success, org.Error);

        var project = await new CreateProjectHandler(HandlerSqlite.Factory(_connection), auth: null, tenant).HandleAsync(new CreateProjectCommand(
            new CreateProjectRequest("Beacon", "desc", org.Value!.Id), actor), TestContext.Current.CancellationToken);
        Assert.True(project.Success, project.Error);

        var orgMember = await _db.OrgMembers.SingleAsync(m => m.OrgId == org.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(user.Id, orgMember.UserId);
        Assert.Equal(MemberRole.Owner, orgMember.Role);

        var projectMember = await _db.ProjectMembers.SingleAsync(m => m.ProjectId == project.Value!.Id, TestContext.Current.CancellationToken);
        Assert.Equal(user.Id, projectMember.UserId);
        Assert.Equal(MemberRole.Owner, projectMember.Role);

        using (TenantScope.EnterProjectScope(Guid.Empty))
        using (TenantScope.EnterOrgScope(Guid.Empty))
        {
            var missed = await new GetCurrentProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(TestContext.Current.CancellationToken);
            Assert.Null(missed.Value);

            var current = await new GetCurrentProjectHandler(HandlerSqlite.Factory(_connection)).HandleAsync(user.Id, isAdmin: false, ct: TestContext.Current.CancellationToken);
            Assert.True(current.Success);
            Assert.NotNull(current.Value);
            Assert.Equal(project.Value!.Id, current.Value!.Id);
        }
    }
}
