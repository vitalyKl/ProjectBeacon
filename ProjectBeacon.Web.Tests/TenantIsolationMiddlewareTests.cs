namespace ProjectBeacon.Web.Tests;

using System.Security.Claims;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class TenantIsolationMiddlewareTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public TenantIsolationMiddlewareTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task StaleCookieProjectId_ReplacedByMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var stale = Project.Create("A", null, org.Id);
        var live = Project.Create("B", null, org.Id);
        _db.Projects.AddRange(stale, live);
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(live.Id, user.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();

        Guid? scoped = null;
        var mw = new TenantIsolationMiddleware(_ =>
        {
            scoped = TenantScope.CurrentProjectId;
            return Task.CompletedTask;
        });

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("isAdmin", "false"),
                new Claim("project_id", stale.Id.ToString())
            ], "Cookies"))
        };

        await mw.InvokeAsync(http, _db);
        Assert.Equal(live.Id, scoped);
    }

    [Fact]
    public async Task CookieProjectId_IgnoredInFavorOfOldestMembership()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var first = Project.Create("A", null, org.Id);
        var second = Project.Create("B", null, org.Id);
        _db.Projects.AddRange(first, second);
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.ProjectMembers.Add(ProjectMember.Create(first.Id, user.Id, MemberRole.Owner));
        _db.ProjectMembers.Add(ProjectMember.Create(second.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync();

        Guid? scoped = null;
        var mw = new TenantIsolationMiddleware(_ =>
        {
            scoped = TenantScope.CurrentProjectId;
            return Task.CompletedTask;
        });

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("isAdmin", "false"),
                new Claim("project_id", second.Id.ToString())
            ], "Cookies"))
        };

        await mw.InvokeAsync(http, _db);
        Assert.Equal(first.Id, scoped);
    }

    [Fact]
    public async Task RemovedFromLastProject_IgnoresStaleCookie()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        Guid? scoped = null;
        var mw = new TenantIsolationMiddleware(_ =>
        {
            scoped = TenantScope.CurrentProjectId;
            return Task.CompletedTask;
        });

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("isAdmin", "false"),
                new Claim("project_id", project.Id.ToString())
            ], "Cookies"))
        };

        await mw.InvokeAsync(http, _db);
        Assert.Equal(Guid.Empty, scoped);
    }

    [Fact]
    public async Task ForeignRouteProjectId_IgnoredForNonMember()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var foreign = Project.Create("A", null, org.Id);
        _db.Projects.Add(foreign);
        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        Guid? scoped = null;
        var mw = new TenantIsolationMiddleware(_ =>
        {
            scoped = TenantScope.CurrentProjectId;
            return Task.CompletedTask;
        });

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("isAdmin", "false")
            ], "Cookies"))
        };
        http.Request.RouteValues["projectId"] = foreign.Id.ToString();

        await mw.InvokeAsync(http, _db);
        Assert.Equal(Guid.Empty, scoped);
    }
}
