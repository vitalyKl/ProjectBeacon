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
}
