namespace ProjectBeacon.Application.Tests;

using Domain.Common;
using Domain.Entities.Audit;
using Domain.Entities.Email;
using Domain.Entities.Email;
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
    public void Model_QueryFilters_AreAppliedExactlyToScopedEntities()
    {
        var scoped = new List<string>();
        foreach (var entityType in _db.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var isScoped = typeof(IProjectScoped).IsAssignableFrom(clr)
                || typeof(IOrgScoped).IsAssignableFrom(clr);
            var isSpecialTenantFiltered = clr == typeof(AuditEvent);
            var isGlobalOperatorOnly = clr == typeof(PlatformEmailSettings) || clr == typeof(EmailDeliveryLog);
            var hasFilter = entityType.GetQueryFilter() is not null;
            var expectsFilter = isScoped || isSpecialTenantFiltered || isGlobalOperatorOnly;
            Assert.True(expectsFilter == hasFilter,
                $"{clr.FullName}: expected query filter={expectsFilter} but hasFilter={hasFilter}");
            if (isScoped || isSpecialTenantFiltered || isGlobalOperatorOnly)
                scoped.Add(clr.Name);
        }

        // Frozen baseline: 26 project-scoped + 3 org-scoped + AuditEvent + 2 operator-only globals.
        Assert.Equal(32, scoped.Count);
    }
}
