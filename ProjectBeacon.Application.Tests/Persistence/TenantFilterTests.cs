namespace ProjectBeacon.Application.Tests;

using Domain.Common;
using Application.Persistence;
using Domain.Entities.Audit;
using Domain.Enums;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public sealed class TenantFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public TenantFilterTests()
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
    public async Task NoScope_ProjectScopedQuery_ReturnsEmpty()
    {
        var org = Org.Create("Org");
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        var task = TaskItem.Create("secret", project.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await _db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
        using (TenantScope.EnterProjectScope(project.Id))
            Assert.Contains(task.Id, await _db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
        using (TenantScope.EnterUnscoped())
            Assert.Contains(task.Id, await _db.Tasks.Select(t => t.Id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitOrgTenant_OverridesAmbientProjectScope()
    {
        var org = Org.Create("Org");
        var project = Project.Create("P", null, org.Id);
        var user = User.Create("owner", "owner@example.test", "hash");
        var task = TaskItem.Create("project task", project.Id);
        var audit = AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.Human,
            user.Id,
            null,
            null,
            "org.updated",
            "Org",
            org.Id.ToString(),
            org.Id,
            null,
            AuditOutcome.Success);

        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.Users.Add(user);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, user.Id, MemberRole.Owner));
        _db.Tasks.Add(task);
        _db.AuditEvents.Add(audit);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using (TenantScope.EnterProjectScope(project.Id))
        {
            var tenant = new TenantContext();
            tenant.Assign(null, org.Id, unscoped: false);
            var options = new DbContextOptionsBuilder<BeaconDbContext>()
                .UseSqlite(_connection)
                .Options;
            await using var orgDb = new BeaconDbContext(options, tenant);

            Assert.Null(orgDb.FilterProjectId);
            Assert.Equal(org.Id, orgDb.FilterOrgId);
            Assert.True(await orgDb.OrgMembers.AnyAsync(
                member => member.OrgId == org.Id && member.UserId == user.Id,
                TestContext.Current.CancellationToken));
            Assert.Single(await orgDb.AuditEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await orgDb.Tasks.ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Model_QueryFilters_AreAppliedExactlyToScopedEntities()
    {
        var scoped = new List<string>();
        foreach (var entityType in _db.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var isScoped = typeof(IProjectScoped).IsAssignableFrom(clr)
                || typeof(IOrgScoped).IsAssignableFrom(clr);
            var isSpecialTenantFiltered = clr == typeof(AuditEvent);
            var hasFilter = entityType.GetQueryFilter() is not null;
            var expectsFilter = isScoped || isSpecialTenantFiltered;
            Assert.True(expectsFilter == hasFilter,
                $"{clr.FullName}: expected query filter={expectsFilter} but hasFilter={hasFilter}");
            if (isScoped || isSpecialTenantFiltered)
                scoped.Add(clr.Name);
        }

        // Frozen baseline: 26 IProjectScoped + 3 IOrgScoped + the special audit scope.
        Assert.Equal(30, scoped.Count);
    }
}
