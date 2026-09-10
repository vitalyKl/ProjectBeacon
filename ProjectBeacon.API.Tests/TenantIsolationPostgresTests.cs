namespace ProjectBeacon.API.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

[Collection("postgres-serial")]
public sealed class TenantIsolationPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public TenantIsolationPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ProjectScope_HidesOtherProjectTasks()
    {
        Guid projectA;
        Guid projectB;
        Guid taskA;
        Guid taskB;

        await using (var seed = _postgres.CreateContext())
        {
            var orgA = Org.Create("Org A");
            var orgB = Org.Create("Org B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync();

            var pA = Project.Create("Project A", null, orgA.Id);
            var pB = Project.Create("Project B", null, orgB.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync();

            var tA = TaskItem.Create("Task A", pA.Id, TaskPriority.Medium, TaskType.Task);
            var tB = TaskItem.Create("Task B", pB.Id, TaskPriority.Medium, TaskType.Task);
            seed.Tasks.AddRange(tA, tB);
            await seed.SaveChangesAsync();

            projectA = pA.Id;
            projectB = pB.Id;
            taskA = tA.Id;
            taskB = tB.Id;
        }

        await using var db = _postgres.CreateContext();

        using (TenantScope.EnterProjectScope(projectA))
        {
            var visible = await db.Tasks.Select(t => t.Id).ToListAsync();
            Assert.Contains(taskA, visible);
            Assert.DoesNotContain(taskB, visible);
        }

        using (TenantScope.EnterProjectScope(projectB))
        {
            var visible = await db.Tasks.Select(t => t.Id).ToListAsync();
            Assert.Contains(taskB, visible);
            Assert.DoesNotContain(taskA, visible);
        }

        var unscoped = await db.Tasks.Select(t => t.Id).ToListAsync();
        Assert.Contains(taskA, unscoped);
        Assert.Contains(taskB, unscoped);
    }

    [Fact]
    public async Task OrgScope_HidesOtherOrgProjects()
    {
        Guid orgA;
        Guid orgB;
        Guid projectA;
        Guid projectB;

        await using (var seed = _postgres.CreateContext())
        {
            var a = Org.Create("Tenant Org A");
            var b = Org.Create("Tenant Org B");
            seed.Orgs.AddRange(a, b);
            await seed.SaveChangesAsync();

            var pA = Project.Create("Tenant Project A", null, a.Id);
            var pB = Project.Create("Tenant Project B", null, b.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync();

            orgA = a.Id;
            orgB = b.Id;
            projectA = pA.Id;
            projectB = pB.Id;
        }

        await using var db = _postgres.CreateContext();

        using (TenantScope.EnterOrgScope(orgA))
        {
            var visible = await db.Projects.Select(p => p.Id).ToListAsync();
            Assert.Contains(projectA, visible);
            Assert.DoesNotContain(projectB, visible);
        }

        using (TenantScope.EnterOrgScope(orgB))
        {
            var visible = await db.Projects.Select(p => p.Id).ToListAsync();
            Assert.Contains(projectB, visible);
            Assert.DoesNotContain(projectA, visible);
        }
    }

    [Fact]
    public async Task FilterAppliesWithoutIgnoreQueryFilters()
    {
        Guid projectA;
        Guid taskB;

        await using (var seed = _postgres.CreateContext())
        {
            var orgA = Org.Create("Filter Org A");
            var orgB = Org.Create("Filter Org B");
            seed.Orgs.AddRange(orgA, orgB);
            await seed.SaveChangesAsync();
            var pA = Project.Create("Filter Project A", null, orgA.Id);
            var pB = Project.Create("Filter Project B", null, orgB.Id);
            seed.Projects.AddRange(pA, pB);
            await seed.SaveChangesAsync();
            seed.Tasks.Add(TaskItem.Create("Keep", pA.Id));
            var other = TaskItem.Create("Secret", pB.Id);
            seed.Tasks.Add(other);
            await seed.SaveChangesAsync();
            projectA = pA.Id;
            taskB = other.Id;
        }

        await using var db = _postgres.CreateContext();
        using (TenantScope.EnterProjectScope(projectA))
        {
            Assert.DoesNotContain(taskB, await db.Tasks.Select(t => t.Id).ToListAsync());
            Assert.Contains(taskB, await db.Tasks.IgnoreQueryFilters().Select(t => t.Id).ToListAsync());
        }
    }
}
