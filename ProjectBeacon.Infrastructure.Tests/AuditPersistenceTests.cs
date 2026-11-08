namespace ProjectBeacon.Infrastructure.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;

public sealed class AuditPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<BeaconDbContext> _options;
    private readonly TenantContext _unscoped;

    public AuditPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;
        _unscoped = new TenantContext();
        _unscoped.Assign(null, null, unscoped: true);

        using var db = new BeaconDbContext(_options, _unscoped);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Writer_StagesAuditAndBusinessMutationOnSameUnitOfWork()
    {
        var user = User.Create("audit-user", "audit@example.com", "hash");
        var auditEvent = CreateEvent("user.created", user.Id);
        await using var db = new BeaconDbContext(_options, _unscoped);

        db.Users.Add(user);
        new EfAuditWriter().Stage(db, auditEvent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var verify = new BeaconDbContext(_options, _unscoped);
        Assert.True(await verify.Users.AnyAsync(x => x.Id == user.Id, TestContext.Current.CancellationToken));
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.Id == auditEvent.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TenantFilter_ExposesOnlyMatchingOrgOrProjectEvents()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var events = new[]
        {
            CreateEvent("org.visible", Guid.NewGuid(), orgId: orgId),
            CreateEvent("project.visible", Guid.NewGuid(), orgId: orgId, projectId: projectId),
            CreateEvent("other-org.hidden", Guid.NewGuid(), orgId: otherOrgId),
            CreateEvent("platform.hidden", Guid.NewGuid())
        };

        await using (var seed = new BeaconDbContext(_options, _unscoped))
        {
            seed.AuditEvents.AddRange(events);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var orgTenant = new TenantContext();
        orgTenant.Assign(null, orgId, unscoped: false);
        await using (var orgDb = new BeaconDbContext(_options, orgTenant))
        {
            var actions = await orgDb.AuditEvents.Select(x => x.Action).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains("org.visible", actions);
            Assert.Contains("project.visible", actions);
            Assert.DoesNotContain("other-org.hidden", actions);
            Assert.DoesNotContain("platform.hidden", actions);
        }

        var projectTenant = new TenantContext();
        projectTenant.Assign(projectId, orgId, unscoped: false);
        await using (var projectDb = new BeaconDbContext(_options, projectTenant))
        {
            var actions = await projectDb.AuditEvents.Select(x => x.Action).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains("org.visible", actions);
            Assert.Contains("project.visible", actions);
            Assert.DoesNotContain("other-org.hidden", actions);
            Assert.DoesNotContain("platform.hidden", actions);
        }
    }

    [Fact]
    public async Task GlobalEvents_RequireExplicitUnscopedContext()
    {
        var global = CreateEvent("operator.login", Guid.NewGuid());
        await using (var seed = new BeaconDbContext(_options, _unscoped))
        {
            seed.AuditEvents.Add(global);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var scoped = new TenantContext();
        scoped.Assign(null, null, unscoped: false);
        await using var scopedDb = new BeaconDbContext(_options, scoped);
        Assert.Empty(await scopedDb.AuditEvents.ToListAsync(TestContext.Current.CancellationToken));

        using (TenantScope.EnterUnscoped())
        {
            Assert.Single(await scopedDb.AuditEvents.ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    private static AuditEvent CreateEvent(string action, Guid actorId, Guid? orgId = null, Guid? projectId = null) =>
        AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.Human,
            actorId,
            null,
            null,
            action,
            "TestTarget",
            Guid.NewGuid().ToString(),
            orgId,
            projectId,
            AuditOutcome.Success);
}
