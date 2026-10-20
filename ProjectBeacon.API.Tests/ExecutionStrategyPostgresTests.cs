namespace ProjectBeacon.API.Tests;

using Application;
using Application.Auth;
using Application.Devices;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The production Npgsql options register EnableRetryOnFailure (AddInfrastructure), whose
/// retrying execution strategy does not support user-initiated transactions unless the whole
/// transaction is wrapped in an execution strategy (the ClaimTaskHandler pattern). These tests
/// run the two transaction paths — finish-work and SaveWorkstation — through handlers resolved
/// from real DI with those real options, so a missing strategy wrapper fails instead of being
/// silently bypassed.
/// </summary>
[Collection("postgres-serial")]
public sealed class ExecutionStrategyPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ExecutionStrategyPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_postgres.AppConnectionString);
        services.AddApplicationHandlers();
        return services.BuildServiceProvider();
    }

    private static async Task<BeaconDbContext> NewAdminAsync(PostgresFixture postgres)
    {
        var admin = new BeaconDbContext(new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options);
        await admin.Database.OpenConnectionAsync();
        return admin;
    }

    // finish-work opens a short transaction (state write + "Work finished" comment commit
    // together). Under the retry-enabled production options that path must complete instead of
    // throwing "does not support user-initiated transactions".
    [Fact]
    public async Task FinishWork_TransactionPath_Succeeds_UnderRetryOptions()
    {
        _postgres.RequirePostgres();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid userId, projectId, orgId, taskId;
        await using (var admin = await NewAdminAsync(_postgres))
        {
            using (TenantScope.EnterUnscoped())
            {
                var user = User.Create($"es-user-{suffix}", $"es-{suffix}@example.test", "hash");
                admin.Users.Add(user);
                var org = Org.Create($"ES Org {suffix}");
                admin.Orgs.Add(org);
                var project = Project.Create($"ES Proj {suffix}", null, org.Id);
                admin.Projects.Add(project);
                var task = TaskItem.Create("ES finish task", project.Id, TaskPriority.Medium, TaskType.Task);
                admin.Tasks.Add(task);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                admin.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                userId = user.Id;
                orgId = org.Id;
                projectId = project.Id;
                taskId = task.Id;
            }
        }

        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Assign(projectId, orgId, unscoped: false);
        var handler = scope.ServiceProvider.GetRequiredService<FinishWorkHandler>();

        var result = await handler.HandleAsync(
            new FinishWorkCommand(new FinishWorkRequest(
                taskId.ToString(), "skipped", "verification output", userId.ToString())),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var verify = await NewAdminAsync(_postgres);
        using (TenantScope.EnterUnscoped())
        {
            Assert.True(await verify.TaskComments.IgnoreQueryFilters()
                .AnyAsync(c => c.TaskId == taskId && c.Content.StartsWith("Work finished: skipped"),
                    TestContext.Current.CancellationToken));
        }
    }

    // SaveWorkstation commits the guarded revision UPDATE and the command INSERT in one
    // transaction. Under the retry-enabled production options that path must complete.
    [Fact]
    public async Task EnqueueCommand_SaveWorkstation_TransactionPath_Succeeds_UnderRetryOptions()
    {
        _postgres.RequirePostgres();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid userId, deviceId;
        await using (var admin = await NewAdminAsync(_postgres))
        {
            using (TenantScope.EnterUnscoped())
            {
                var user = User.Create($"es-dev-user-{suffix}", $"es-dev-{suffix}@example.test", "hash");
                admin.Users.Add(user);
                var device = DaemonDevice.Create($"es-device-{suffix}", user.Id, $"fp-{suffix}",
                    OpaqueToken.Hash($"device-token-{suffix}"), "bcd_es");
                device.Heartbeat();
                admin.DaemonDevices.Add(device);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                userId = user.Id;
                deviceId = device.Id;
            }
        }

        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<EnqueueCommandHandler>();

        var result = await handler.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, userId, WorkstationCommandKind.SaveWorkstation,
                "{\"revision\":0,\"theme\":\"dark\"}", null)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var verify = await NewAdminAsync(_postgres);
        using (TenantScope.EnterUnscoped())
        {
            var stored = await verify.DaemonDevices.IgnoreQueryFilters()
                .FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, stored.DesiredRevision);
            Assert.Contains("\"dark\"", stored.DesiredWorkstationJson);
        }
    }

    // A transient save failure must be retried by the production strategy on the same
    // context. The handler detaches the failed attempt's tracked command before re-entering
    // the lambda, so exactly one command row lands.
    [Fact]
    public async Task EnqueueCommand_SaveWorkstation_TransientSaveFailure_Retries_WithoutDuplicateCommand()
    {
        _postgres.RequirePostgres();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid userId, deviceId;
        await using (var admin = await NewAdminAsync(_postgres))
        {
            using (TenantScope.EnterUnscoped())
            {
                var user = User.Create($"es-flaky-user-{suffix}", $"es-flaky-{suffix}@example.test", "hash");
                admin.Users.Add(user);
                var device = DaemonDevice.Create($"es-flaky-device-{suffix}", user.Id, $"fp-flaky-{suffix}",
                    OpaqueToken.Hash($"device-token-{suffix}"), "bcd_es");
                device.Heartbeat();
                admin.DaemonDevices.Add(device);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                userId = user.Id;
                deviceId = device.Id;
            }
        }

        using var provider = BuildProviderWithTransientFirstSave();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<EnqueueCommandHandler>();

        var result = await handler.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, userId, WorkstationCommandKind.SaveWorkstation,
                "{\"revision\":0,\"theme\":\"dark\"}", null)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var verify = await NewAdminAsync(_postgres);
        using (TenantScope.EnterUnscoped())
        {
            var stored = await verify.DaemonDevices.IgnoreQueryFilters()
                .FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, stored.DesiredRevision);
            // The retried save must not land a second command row for the same save.
            Assert.Equal(1, await verify.WorkstationCommands.IgnoreQueryFilters()
                .CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    // A transient save failure on the "Work finished" comment insert must be retried
    // without duplicating the comment: the handler detaches the failed attempt's tracked
    // comment and re-reads the task before re-entering the lambda.
    [Fact]
    public async Task FinishWork_TransientSaveFailure_Retries_WithoutDuplicateComment()
    {
        _postgres.RequirePostgres();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid userId, projectId, orgId, taskId;
        await using (var admin = await NewAdminAsync(_postgres))
        {
            using (TenantScope.EnterUnscoped())
            {
                var user = User.Create($"es-flaky-user-{suffix}", $"es-flaky-{suffix}@example.test", "hash");
                admin.Users.Add(user);
                var org = Org.Create($"ES Flaky Org {suffix}");
                admin.Orgs.Add(org);
                var project = Project.Create($"ES Flaky Proj {suffix}", null, org.Id);
                admin.Projects.Add(project);
                var task = TaskItem.Create("ES flaky finish task", project.Id, TaskPriority.Medium, TaskType.Task);
                admin.Tasks.Add(task);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                admin.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                userId = user.Id;
                orgId = org.Id;
                projectId = project.Id;
                taskId = task.Id;
            }
        }

        using var provider = BuildProviderWithTransientFirstSave();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>()
            .Assign(projectId, orgId, unscoped: false);
        var handler = scope.ServiceProvider.GetRequiredService<FinishWorkHandler>();

        var result = await handler.HandleAsync(
            new FinishWorkCommand(new FinishWorkRequest(
                taskId.ToString(), "skipped", "verification output", userId.ToString())),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var verify = await NewAdminAsync(_postgres);
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await verify.TaskComments.IgnoreQueryFilters()
                .CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            var current = await verify.Tasks.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(1, current.Description!.Split("\n\n").Count(p => p == "Note: verification output"));
        }
    }

    private ServiceProvider BuildProviderWithTransientFirstSave()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_postgres.AppConnectionString);
        services.AddApplicationHandlers();
        services.AddScoped<IBeaconDbFactory>(sp => new TransientFirstSaveFactory(
            sp.GetRequiredService<DbContextOptions<BeaconDbContext>>(),
            sp.GetRequiredService<ITenantContext>()));
        return services.BuildServiceProvider();
    }

    // First SaveChangesAsync on each context throws a TimeoutException, which the
    // production Npgsql retrying strategy (EnableRetryOnFailure) classifies as transient
    // and retries on the same context — the state a real transient write failure leaves
    // behind for the retry.
    private sealed class TransientFirstSaveFactory : IBeaconDbFactory
    {
        private readonly DbContextOptions<BeaconDbContext> _options;
        private readonly ITenantContext _tenant;

        public TransientFirstSaveFactory(DbContextOptions<BeaconDbContext> options, ITenantContext tenant)
        {
            _options = options;
            _tenant = tenant;
        }

        public IBeaconDb CreateDbContext() => Create(_tenant);

        public IBeaconDb CreateDbContext(ITenantContext tenant) => Create(tenant);

        private IBeaconDb Create(ITenantContext tenant)
        {
            var db = new TransientFirstSaveContext(_options, tenant);
            // Mirror BeaconDbFactory.ReconcileTenantRls: apply the tenant GUCs only when
            // the bound tenant differs from the AsyncLocal scope the interceptor saw.
            if (!TenantScope.IsUnscoped
                && (tenant.Unscoped
                    || tenant.ProjectId != TenantScope.CurrentProjectId
                    || tenant.OrgId != TenantScope.CurrentOrgId))
            {
                TenantRlsSession.Apply(db, tenant.ProjectId, tenant.OrgId, tenant.Unscoped);
            }
            return db;
        }
    }

    private sealed class TransientFirstSaveContext : BeaconDbContext
    {
        private bool _failNextSave = true;

        public TransientFirstSaveContext(DbContextOptions<BeaconDbContext> options, ITenantContext tenant)
            : base(options, tenant)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_failNextSave)
            {
                _failNextSave = false;
                // Nothing reached the server, but the added entities stay in the change
                // tracker — the exact state a real transient failure leaves for the retry.
                return Task.FromException<int>(new TimeoutException("simulated transient save failure"));
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
