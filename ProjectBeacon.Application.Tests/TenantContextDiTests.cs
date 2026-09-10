namespace ProjectBeacon.Application.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class TenantContextDiTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public TenantContextDiTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<BeaconDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<BeaconDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ScopedTenantContext_IsolatesWithoutAsyncLocal()
    {
        Guid taskA;
        Guid taskB;
        Guid projectA;
        Guid orgA;

        using (var seedScope = _provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var org = Org.Create("Org");
                db.Orgs.Add(org);
                await db.SaveChangesAsync();
                orgA = org.Id;
                var pA = Project.Create("A", null, org.Id);
                var pB = Project.Create("B", null, org.Id);
                db.Projects.AddRange(pA, pB);
                await db.SaveChangesAsync();
                projectA = pA.Id;
                var tA = TaskItem.Create("keep", pA.Id);
                var tB = TaskItem.Create("secret", pB.Id);
                db.Tasks.AddRange(tA, tB);
                await db.SaveChangesAsync();
                taskA = tA.Id;
                taskB = tB.Id;
            }
        }

        using var scope = _provider.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Assign(projectA, orgA, unscoped: false);
        var filtered = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();

        Assert.Null(TenantScope.CurrentProjectId);
        Assert.False(TenantScope.IsUnscoped);
        var visible = await filtered.Tasks.Select(t => t.Id).ToListAsync();
        Assert.Contains(taskA, visible);
        Assert.DoesNotContain(taskB, visible);
    }
}
