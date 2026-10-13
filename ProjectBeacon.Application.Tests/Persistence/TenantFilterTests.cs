namespace ProjectBeacon.Application.Tests;

using Domain.Common;
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
        await _db.SaveChangesAsync();
        var project = Project.Create("P", null, org.Id);
        _db.Projects.Add(project);
        var task = TaskItem.Create("secret", project.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.Tasks.Select(t => t.Id).ToListAsync());
        using (TenantScope.EnterProjectScope(project.Id))
            Assert.Contains(task.Id, await _db.Tasks.Select(t => t.Id).ToListAsync());
        using (TenantScope.EnterUnscoped())
            Assert.Contains(task.Id, await _db.Tasks.Select(t => t.Id).ToListAsync());
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
            var hasFilter = entityType.GetQueryFilter() is not null;
            Assert.True(isScoped == hasFilter, $"{clr.FullName}: expected scoped={isScoped} but hasFilter={hasFilter}");
            if (isScoped)
                scoped.Add(clr.Name);
        }

        // Frozen baseline: 23 IProjectScoped + 3 IOrgScoped.
        Assert.Equal(26, scoped.Count);
    }
}
