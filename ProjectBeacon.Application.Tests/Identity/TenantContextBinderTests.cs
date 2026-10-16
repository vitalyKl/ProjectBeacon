namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;

public sealed class TenantContextBinderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public TenantContextBinderTests()
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
    public async Task BindUserAsync_UsesOldestMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = Project.Create("First", null, org.Id);
        var second = Project.Create("Second", null, org.Id);
        _db.Projects.AddRange(first, second);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ProjectMembers.Add(ProjectMember.Create(first.Id, user.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(second.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: false), TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, tenant.ProjectId);
        Assert.Equal(org.Id, tenant.OrgId);
        Assert.False(tenant.Unscoped);
    }

    [Fact]
    public async Task BindUserAsync_AdminWithoutMembership_UsesOldestProject()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("Only", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = User.Create("root", "root@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: true), TestContext.Current.CancellationToken);

        Assert.Equal(project.Id, tenant.ProjectId);
        Assert.Equal(org.Id, tenant.OrgId);
        Assert.False(tenant.Unscoped);
    }

    [Fact]
    public async Task BindUserAsync_NonAdminWithoutMembership_StaysFailClosed()
    {
        var user = User.Create("bob", "bob@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: false), TestContext.Current.CancellationToken);

        Assert.Null(tenant.ProjectId);
        Assert.Null(tenant.OrgId);
        Assert.False(tenant.Unscoped);
    }

    [Fact]
    public async Task BindUserAsync_HonorsValidProjectClaim()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = Project.Create("First", null, org.Id);
        var second = Project.Create("Second", null, org.Id);
        _db.Projects.AddRange(first, second);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = User.Create("erin", "erin@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ProjectMembers.Add(ProjectMember.Create(first.Id, user.Id, MemberRole.Member));
        _db.ProjectMembers.Add(ProjectMember.Create(second.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: false, projectClaim: second.Id), TestContext.Current.CancellationToken);

        Assert.Equal(second.Id, tenant.ProjectId);
        Assert.Equal(org.Id, tenant.OrgId);
    }

    [Fact]
    public async Task BindUserAsync_IgnoresStaleProjectClaim()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = Project.Create("First", null, org.Id);
        _db.Projects.Add(first);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = User.Create("frank", "frank@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ProjectMembers.Add(ProjectMember.Create(first.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var tenant = new TenantContext();
        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: false, projectClaim: Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, tenant.ProjectId);
        Assert.Equal(org.Id, tenant.OrgId);
    }

    [Fact]
    public async Task BindUserAsync_AlreadyAssigned_DoesNotOverride()
    {
        var existing = Guid.NewGuid();
        var tenant = new TenantContext();
        tenant.Assign(existing, existing, unscoped: false);

        var user = User.Create("carol", "carol@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: true), TestContext.Current.CancellationToken);

        Assert.Equal(existing, tenant.ProjectId);
        Assert.Equal(existing, tenant.OrgId);
    }

    [Fact]
    public async Task BindUserAsync_Unscoped_DoesNotOverride()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);

        var user = User.Create("dave", "dave@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var binder = new TenantContextBinder(HandlerSqlite.Factory(_connection, tenant), tenant);

        await binder.BindUserAsync(Actor(user.Id, isAdmin: true), TestContext.Current.CancellationToken);

        Assert.Null(tenant.ProjectId);
        Assert.Null(tenant.OrgId);
        Assert.True(tenant.Unscoped);
    }

    private static ActorContext Actor(Guid userId, bool isAdmin, Guid? projectClaim = null) =>
        new(ActorType.Human, userId, isAdmin, null, null, projectClaim, null, ApiTokenCapability.None);
}
