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
    public async Task ValidClaimProjectId_HonoredOverOldestMembership()
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
        Assert.Equal(second.Id, scoped);
    }

    [Fact]
    public async Task AdminClaimProjectId_HonoredWhenProjectExists()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        var user = User.Create("admin", "admin@example.com", "hash", true);
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
                new Claim("isAdmin", "true"),
                new Claim("project_id", project.Id.ToString())
            ], "Cookies"))
        };

        await mw.InvokeAsync(http, _db);
        Assert.Equal(project.Id, scoped);
    }

    [Fact]
    public async Task AdminStaleClaim_FallsBackToOldestProject()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var oldest = Project.Create("A", null, org.Id);
        var newer = Project.Create("B", null, org.Id);
        _db.Projects.AddRange(oldest, newer);
        var user = User.Create("admin", "admin@example.com", "hash", true);
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
                new Claim("isAdmin", "true"),
                new Claim("project_id", Guid.NewGuid().ToString())
            ], "Cookies"))
        };

        await mw.InvokeAsync(http, _db);
        Assert.Equal(oldest.Id, scoped);
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

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void FromRoute_ReturnsParsedId_ForValidRouteValue(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues["projectId"] = value;

        Assert.Equal(Guid.Parse(value), TenantIsolationMiddleware.FromRoute(http, "projectId"));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void FromRoute_ReturnsNull_ForInvalidRouteValue(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues["projectId"] = value;

        Assert.Null(TenantIsolationMiddleware.FromRoute(http, "projectId"));
    }

    [Fact]
    public void FromRoute_ReturnsNull_WhenRouteValueAbsent()
    {
        var http = new DefaultHttpContext();

        Assert.Null(TenantIsolationMiddleware.FromRoute(http, "projectId"));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void FromHeader_ReturnsParsedId_ForValidHeaderValue(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Project-Id"] = value;

        Assert.Equal(Guid.Parse(value), TenantIsolationMiddleware.FromHeader(http, "X-Project-Id"));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void FromHeader_ReturnsNull_ForInvalidHeaderValue(string value)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Project-Id"] = value;

        Assert.Null(TenantIsolationMiddleware.FromHeader(http, "X-Project-Id"));
    }

    [Fact]
    public void FromHeader_ReturnsNull_WhenHeaderAbsent()
    {
        var http = new DefaultHttpContext();

        Assert.Null(TenantIsolationMiddleware.FromHeader(http, "X-Project-Id"));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void FromClaim_ReturnsParsedId_ForValidClaim(string value)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("project_id", value)], "Cookies"))
        };

        Assert.Equal(Guid.Parse(value), TenantIsolationMiddleware.FromClaim(http, "project_id"));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void FromClaim_ReturnsNull_ForInvalidClaim(string value)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("project_id", value)], "Cookies"))
        };

        Assert.Null(TenantIsolationMiddleware.FromClaim(http, "project_id"));
    }

    [Fact]
    public void FromClaim_ReturnsNull_WhenClaimAbsent()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity("Cookies"))
        };

        Assert.Null(TenantIsolationMiddleware.FromClaim(http, "project_id"));
    }

    [Fact]
    public void IsMemberOf_True_WhenMembershipExists()
    {
        var member = ProjectMember.Create(Guid.NewGuid(), Guid.NewGuid(), MemberRole.Member);
        var members = new List<ProjectMember> { member };

        Assert.True(TenantIsolationMiddleware.IsMemberOf(members, member.ProjectId));
    }

    [Fact]
    public void IsMemberOf_False_WhenNoMembership()
    {
        var member = ProjectMember.Create(Guid.NewGuid(), Guid.NewGuid(), MemberRole.Member);
        var members = new List<ProjectMember> { member };

        Assert.False(TenantIsolationMiddleware.IsMemberOf(members, Guid.NewGuid()));
    }

    [Fact]
    public void FindMembership_ReturnsMember_WhenPresent()
    {
        var project = Guid.NewGuid();
        var member = ProjectMember.Create(project, Guid.NewGuid(), MemberRole.Owner);
        var members = new List<ProjectMember> { member };

        var found = TenantIsolationMiddleware.FindMembership(members, project);

        Assert.NotNull(found);
        Assert.Equal(project, found!.ProjectId);
    }

    [Fact]
    public void FindMembership_ReturnsNull_WhenAbsent()
    {
        var member = ProjectMember.Create(Guid.NewGuid(), Guid.NewGuid(), MemberRole.Member);
        var members = new List<ProjectMember> { member };

        Assert.Null(TenantIsolationMiddleware.FindMembership(members, Guid.NewGuid()));
    }

    [Fact]
    public void FindMembership_ReturnsNull_ForNullProjectId()
    {
        var member = ProjectMember.Create(Guid.NewGuid(), Guid.NewGuid(), MemberRole.Member);
        var members = new List<ProjectMember> { member };

        Assert.Null(TenantIsolationMiddleware.FindMembership(members, null));
    }

    [Fact]
    public async Task ResolveAdminFallbackOrgAsync_ReturnsOrg_ForExistingProjectWhenAdmin()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var result = await TenantIsolationMiddleware.ResolveAdminFallbackOrgAsync(_db, isAdmin: true, project.Id);

        Assert.Equal(org.Id, result);
    }

    [Fact]
    public async Task ResolveAdminFallbackOrgAsync_ReturnsNull_ForMissingProject()
    {
        var result = await TenantIsolationMiddleware.ResolveAdminFallbackOrgAsync(_db, isAdmin: true, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAdminFallbackOrgAsync_ReturnsNull_ForNonAdmin()
    {
        var org = Org.Create("Org", null);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        var project = Project.Create("A", null, org.Id);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var result = await TenantIsolationMiddleware.ResolveAdminFallbackOrgAsync(_db, isAdmin: false, project.Id);

        Assert.Null(result);
    }
}
