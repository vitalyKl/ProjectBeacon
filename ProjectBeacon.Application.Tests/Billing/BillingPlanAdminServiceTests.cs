namespace ProjectBeacon.Application.Tests.Billing;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Billing;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;

public sealed class BillingPlanAdminServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly ProjectBeacon.Infrastructure.Data.BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly IBeaconDbFactory _factory;
    private readonly User _billing;

    public BillingPlanAdminServiceTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();
        _factory = HandlerSqlite.Factory(_connection);

        _billing = User.Create("billing-operator", "billing@example.test", "hash");
        _billing.AssignOperatorRole(OperatorRole.Billing.ToString());
        _billing.BeginTotp("test-secret-cipher");
        _billing.ConfirmTotp();
        _seedDb.Users.Add(_billing);
        _seedDb.SaveChanges();
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Save_StoresProviderNeutralPlanWithoutPrice_AndAuditsMutation()
    {
        var service = CreateService();
        var actor = await ResolveActorAsync(
            _billing,
            OperatorPermission.BillingManage,
            elevated: true);

        var saved = await service.SaveAsync(
            new SaveBillingPlanRequest(
                "alpha-team",
                "Alpha team",
                BillingCadence.Monthly,
                PriceMinor: null,
                Currency: null,
                new Dictionary<string, long> { ["seats"] = 5 },
                ["core", "chat"],
                IsActive: true),
            actor,
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha-team", saved.Key);
        Assert.Equal("Alpha team", saved.Name);
        Assert.Equal(BillingCadence.Monthly, saved.Cadence);
        Assert.Null(saved.PriceMinor);
        Assert.Null(saved.Currency);
        Assert.True(saved.IsActive);
        Assert.Contains("core", saved.Features);
        Assert.Contains("chat", saved.Features);
        Assert.Equal(5, saved.Limits["seats"]);

        var plans = await service.ListAsync(
            await ResolveActorAsync(_billing, OperatorPermission.BillingRead, elevated: false),
            TestContext.Current.CancellationToken);
        var listed = Assert.Single(plans);
        Assert.Equal(saved, listed);

        var audit = await _seedDb.AuditEvents.IgnoreQueryFilters()
            .SingleAsync(item => item.Action == "platform.billing_plan.created",
                TestContext.Current.CancellationToken);
        Assert.Equal("BillingPlan", audit.TargetType);
        Assert.Equal("alpha-team", audit.TargetId);
    }

    [Fact]
    public async Task Save_UpdatesExistingPlanWithoutChangingItsStableKey()
    {
        var service = CreateService();
        var manager = await ResolveActorAsync(_billing, OperatorPermission.BillingManage, elevated: true);
        await service.SaveAsync(
            new SaveBillingPlanRequest(
                "standard",
                "Standard",
                BillingCadence.Monthly,
                null,
                null,
                new Dictionary<string, long>(),
                ["core"],
                true),
            manager,
            TestContext.Current.CancellationToken);

        var updated = await service.SaveAsync(
            new SaveBillingPlanRequest(
                "standard",
                "Standard plus",
                BillingCadence.Annual,
                12000,
                "EUR",
                new Dictionary<string, long> { ["seats"] = 20 },
                ["core", "chat"],
                false),
            manager,
            TestContext.Current.CancellationToken);

        Assert.Equal("standard", updated.Key);
        Assert.Equal("Standard plus", updated.Name);
        Assert.Equal(BillingCadence.Annual, updated.Cadence);
        Assert.Equal(12000, updated.PriceMinor);
        Assert.Equal("EUR", updated.Currency);
        Assert.False(updated.IsActive);

        Assert.Equal(1, await _seedDb.BillingPlans.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _seedDb.AuditEvents.IgnoreQueryFilters()
            .CountAsync(item => item.Action == "platform.billing_plan.created"
                || item.Action == "platform.billing_plan.updated",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_RequiresRecentAuthenticatorStepUp()
    {
        var service = CreateService();
        var actor = await ResolveActorAsync(_billing, OperatorPermission.BillingManage, elevated: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(
            ValidRequest(),
            actor,
            TestContext.Current.CancellationToken));

        Assert.Empty(await _seedDb.BillingPlans.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuditorCanReadPlansButCannotManageThem()
    {
        var auditor = User.Create("auditor-operator", "auditor@example.test", "hash");
        auditor.AssignOperatorRole(OperatorRole.Auditor.ToString());
        _seedDb.Users.Add(auditor);
        await _seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = CreateService();
        var reader = await ResolveActorAsync(auditor, OperatorPermission.BillingRead, elevated: false);
        Assert.Empty(await service.ListAsync(reader, TestContext.Current.CancellationToken));

        var noManage = await ResolveActorAsync(auditor, OperatorPermission.OrganizationsRead, elevated: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(
            ValidRequest(),
            noManage,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("missing-core")]
    [InlineData("duplicate-feature")]
    [InlineData("currency-without-price")]
    [InlineData("negative-limit")]
    [InlineData("invalid-currency")]
    public async Task Save_RejectsInvalidPlanCommercialAndFeatureContracts(string scenario)
    {
        var request = scenario switch
        {
            "missing-core" => ValidRequest() with { Features = ["chat"] },
            "duplicate-feature" => ValidRequest() with { Features = ["core", "CHAT", "chat"] },
            "currency-without-price" => ValidRequest() with { Currency = "EUR" },
            "negative-limit" => ValidRequest() with { Limits = new Dictionary<string, long> { ["seats"] = -1 } },
            "invalid-currency" => ValidRequest() with { PriceMinor = 1000, Currency = "EU" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var service = CreateService();
        var actor = await ResolveActorAsync(_billing, OperatorPermission.BillingManage, elevated: true);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            request,
            actor,
            TestContext.Current.CancellationToken));
        Assert.Empty(await _seedDb.BillingPlans.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_RejectsMalformedPlanKey()
    {
        var service = CreateService();
        var actor = await ResolveActorAsync(_billing, OperatorPermission.BillingManage, elevated: true);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            ValidRequest() with { Key = "UPPER CASE" },
            actor,
            TestContext.Current.CancellationToken));
    }

    private BillingPlanAdminService CreateService() =>
        new(_factory, new EfAuditWriter(), TimeProvider.System);

    private static SaveBillingPlanRequest ValidRequest() =>
        new(
            "alpha",
            "Alpha plan",
            BillingCadence.Monthly,
            null,
            null,
            new Dictionary<string, long>(),
            ["core"],
            true);

    private async Task<AdminActor> ResolveActorAsync(
        User user,
        OperatorPermission permission,
        bool elevated)
    {
        var claimExpiry = elevated ? DateTimeOffset.UtcNow.AddMinutes(5) : (DateTimeOffset?)null;
        var actor = new ProjectBeacon.Application.Authorization.ActorContext(
            ActorType.Human,
            user.Id,
            IsAdmin: true,
            null,
            null,
            null,
            null,
            ApiTokenCapability.None,
            claimExpiry);
        var gate = new DatabaseAdminGate(_factory);
        var access = await gate.RequireAsync(
            actor,
            permission,
            ct: TestContext.Current.CancellationToken);

        Assert.True(access.Success, access.Error);
        return access.Value!;
    }
}
