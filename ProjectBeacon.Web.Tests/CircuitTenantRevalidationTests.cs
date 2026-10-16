using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Web.Extensions;

namespace ProjectBeacon.Web.Tests;

using Application.Persistence;
using Infrastructure.Data;

/// <summary>
/// P1.5: circuit identity/tenant isolation. Verifies the live revalidation that bounds
/// the exposure of a long-lived InteractiveServer circuit after sign-out, revocation,
/// or membership removal.
/// </summary>
public sealed class CircuitTenantRevalidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly DbContextOptions<BeaconDbContext> _options;

    public CircuitTenantRevalidationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options;

        var seedTenant = new TenantContext();
        seedTenant.Assign(null, null, true);
        _db = new BeaconDbContext(_options, seedTenant);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed record CircuitScope(IServiceProvider Scope, TenantContext Tenant);

    private CircuitScope OpenScope(Guid? boundProjectId, Guid? boundOrgId)
    {
        var tenant = new TenantContext();
        tenant.Assign(boundProjectId, boundOrgId, unscoped: false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => tenant);
        services.AddScoped<IBeaconDbFactory>(_ => new BeaconDbFactory(_options, tenant));
        services.AddScoped<CircuitRevalidatingAuthenticationStateProvider>();
        services.AddScoped<CircuitTenantRebindHandler>();
        var root = services.BuildServiceProvider();
        var scope = root.CreateScope();
        return new CircuitScope(scope.ServiceProvider, tenant);
    }

    [Fact]
    public async Task ValidMembership_CurrentProject_KeepsSession()
    {
        var (org, user, project) = await SeedUserInProjectAsync();

        var circuit = OpenScope(project.Id, org.Id);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, Principal(user.Id, isAdmin: false, projectId: project.Id), null, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(project.Id, circuit.Tenant.ProjectId);
        Assert.Equal(org.Id, circuit.Tenant.OrgId);
        Assert.False(circuit.Tenant.Unscoped);
    }

    [Fact]
    public async Task MembershipRemoved_OtherMembershipKept_RebindsTenant()
    {
        var (org, user, current) = await SeedUserInProjectAsync();
        var other = Project.Create("Other", null, org.Id);
        _db.Projects.Add(other);
        _db.ProjectMembers.Add(ProjectMember.Create(other.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.ProjectMembers.Remove(_db.ProjectMembers.Single(m => m.ProjectId == current.Id && m.UserId == user.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuit = OpenScope(current.Id, org.Id);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, Principal(user.Id, isAdmin: false, projectId: current.Id), null, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(other.Id, circuit.Tenant.ProjectId);
    }

    [Fact]
    public async Task AllMembershipsRemoved_ReturnsFalse()
    {
        var (_, user, project) = await SeedUserInProjectAsync();
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.UserId == user.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuit = OpenScope(project.Id, null);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, Principal(user.Id, isAdmin: false, projectId: project.Id), null, TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task UserDeleted_ReturnsFalse()
    {
        var (_, user, project) = await SeedUserInProjectAsync();
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuit = OpenScope(project.Id, null);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, Principal(user.Id, isAdmin: false, projectId: project.Id), null, TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task AnonymousPrincipal_ReturnsFalse()
    {
        var circuit = OpenScope(Guid.NewGuid(), null);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, new ClaimsPrincipal(new ClaimsIdentity()), null, TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task AdminWithoutMembership_BindsOldestProject()
    {
        var (org, user, _) = await SeedUserInProjectAsync();
        _db.ProjectMembers.RemoveRange(_db.ProjectMembers.Where(m => m.UserId == user.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuit = OpenScope(null, null);
        var ok = await CircuitTenantRevalidation.RevalidateAsync(
            circuit.Scope, Principal(user.Id, isAdmin: true, projectId: null), null, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(_db.Projects.IgnoreQueryFilters().OrderBy(p => p.CreatedAt).First().Id, circuit.Tenant.ProjectId);
    }

    [Fact]
    public async Task DbFailure_KeepsSession_FailClosedScopeUnchanged()
    {
        var (org, user, project) = await SeedUserInProjectAsync();
        var circuit = OpenScope(project.Id, org.Id);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantContext>(_ => circuit.Tenant);
        services.AddScoped<IBeaconDbFactory>(_ => new ThrowingDbFactory());
        var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();

        var ok = await CircuitTenantRevalidation.RevalidateAsync(scope.ServiceProvider, Principal(user.Id, false, project.Id), null, TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(project.Id, circuit.Tenant.ProjectId);
    }

    [Fact]
    public async Task TwoCircuits_NoCrossUserTenantLeak()
    {
        var (org, alice, projectA) = await SeedUserInProjectAsync();
        var bob = User.Create("bob", "bob@example.com", "hash");
        var projectB = Project.Create("BobProject", null, org.Id);
        _db.Users.Add(bob);
        _db.Projects.Add(projectB);
        _db.ProjectMembers.Add(ProjectMember.Create(projectB.Id, bob.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuitA = OpenScope(projectA.Id, org.Id);
        var circuitB = OpenScope(projectB.Id, org.Id);

        var okA = await CircuitTenantRevalidation.RevalidateAsync(circuitA.Scope, Principal(alice.Id, false, projectA.Id), null, TestContext.Current.CancellationToken);
        var okB = await CircuitTenantRevalidation.RevalidateAsync(circuitB.Scope, Principal(bob.Id, false, projectB.Id), null, TestContext.Current.CancellationToken);

        Assert.True(okA);
        Assert.True(okB);
        Assert.Equal(projectA.Id, circuitA.Tenant.ProjectId);
        Assert.Equal(projectB.Id, circuitB.Tenant.ProjectId);
        Assert.NotSame(circuitA.Tenant, circuitB.Tenant);
    }

    [Fact]
    public async Task Reconnect_MembershipChanged_RebindsImmediately()
    {
        var (org, user, current) = await SeedUserInProjectAsync();
        var other = Project.Create("Other", null, org.Id);
        _db.Projects.Add(other);
        _db.ProjectMembers.Add(ProjectMember.Create(other.Id, user.Id, MemberRole.Member));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var circuit = OpenScope(current.Id, org.Id);
        var auth = (CircuitRevalidatingAuthenticationStateProvider)circuit.Scope.GetRequiredService<CircuitRevalidatingAuthenticationStateProvider>();
        auth.SetAuthenticationState(Task.FromResult(new AuthenticationState(Principal(user.Id, false, current.Id))));

        _db.ProjectMembers.Remove(_db.ProjectMembers.Single(m => m.ProjectId == current.Id && m.UserId == user.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = circuit.Scope.GetRequiredService<CircuitTenantRebindHandler>();
        await handler.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);

        Assert.Equal(other.Id, circuit.Tenant.ProjectId);
    }

    [Fact]
    public async Task Reconnect_UserDeleted_FlipsCircuitToAnonymous()
    {
        var (_, user, project) = await SeedUserInProjectAsync();

        var circuit = OpenScope(project.Id, null);
        var auth = (CircuitRevalidatingAuthenticationStateProvider)circuit.Scope.GetRequiredService<CircuitRevalidatingAuthenticationStateProvider>();
        auth.SetAuthenticationState(Task.FromResult(new AuthenticationState(Principal(user.Id, false, project.Id))));

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = circuit.Scope.GetRequiredService<CircuitTenantRebindHandler>();
        await handler.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);

        var state = await auth.GetAuthenticationStateAsync();
        Assert.False(state.User.Identity!.IsAuthenticated);
    }

    [Fact]
    public void RevalidationInterval_IsSixtySeconds()
    {
        var scope = OpenScope(null, null);
        var provider = scope.Scope.GetRequiredService<CircuitRevalidatingAuthenticationStateProvider>();

        var property = typeof(CircuitRevalidatingAuthenticationStateProvider)
            .GetProperty("RevalidationInterval",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(TimeSpan.FromSeconds(60), property.GetValue(provider));
    }

    private async Task<(Org Org, User User, Project Project)> SeedUserInProjectAsync()
    {
        var org = Org.Create("Acme");
        _db.Orgs.Add(org);

        var user = User.Create("alice", "alice@example.com", "hash");
        _db.Users.Add(user);

        var project = Project.Create("AcmeProject", null, org.Id);
        _db.Projects.Add(project);

        _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
        await _db.SaveChangesAsync();
        return (org, user, project);
    }

    private static ClaimsPrincipal Principal(Guid userId, bool isAdmin, Guid? projectId)
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        if (isAdmin)
            identity.AddClaim(new Claim("isAdmin", "true"));
        if (projectId is { } p)
            identity.AddClaim(new Claim("project_id", p.ToString()));
        return new ClaimsPrincipal(identity);
    }

    private sealed class ThrowingDbFactory : IBeaconDbFactory
    {
        public IBeaconDb CreateDbContext() => throw new InvalidOperationException("database unavailable");
    }
}
