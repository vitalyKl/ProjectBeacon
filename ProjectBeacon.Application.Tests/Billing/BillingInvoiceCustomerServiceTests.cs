namespace ProjectBeacon.Application.Tests.Billing;

using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Billing;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class BillingInvoiceCustomerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _seedDb;
    private readonly IDisposable _unscoped;
    private readonly Org _orgA;
    private readonly Org _orgB;
    private readonly User _ownerA;
    private readonly User _memberA;
    private readonly TenantContext _tenant;
    private readonly BillingInvoiceCustomerService _service;

    public BillingInvoiceCustomerServiceTests()
    {
        (_connection, _seedDb, _unscoped) = HandlerSqlite.Open();

        _orgA = Org.Create("Billing A");
        _orgB = Org.Create("Billing B");
        _ownerA = User.Create("billing-owner-a", "billing-owner-a@example.test", "hash");
        _memberA = User.Create("billing-member-a", "billing-member-a@example.test", "hash");
        var ownerB = User.Create("billing-owner-b", "billing-owner-b@example.test", "hash");
        _seedDb.Orgs.AddRange(_orgA, _orgB);
        _seedDb.Users.AddRange(_ownerA, _memberA, ownerB);
        _seedDb.SaveChanges();

        _seedDb.OrgMembers.AddRange(
            OrgMember.Create(_orgA.Id, _ownerA.Id, MemberRole.Owner),
            OrgMember.Create(_orgA.Id, _memberA.Id, MemberRole.Member),
            OrgMember.Create(_orgB.Id, ownerB.Id, MemberRole.Owner));
        _seedDb.SaveChanges();

        _seedDb.Database.ExecuteSqlRaw("""
            CREATE TABLE "BillingInvoices" (
                "Id" TEXT NOT NULL,
                "OrgId" TEXT NOT NULL,
                "Number" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "AmountMinor" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                "IssuedAtUtc" TEXT NOT NULL,
                "DueAtUtc" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "PaidAtUtc" TEXT NULL,
                "PaymentReference" TEXT NULL,
                "ProviderKey" TEXT NULL,
                "ExternalInvoiceReference" TEXT NULL
            )
            """);
        InsertInvoice(_orgA.Id, "PB-2026-000001");
        InsertInvoice(_orgB.Id, "PB-2026-000002");

        _tenant = new TenantContext();
        _tenant.Assign(null, _orgA.Id, unscoped: false);
        _tenant.SetLifecycleStatus(OrganizationLifecycleStatus.Active, enforced: true);
        _service = new BillingInvoiceCustomerService(
            HandlerSqlite.Factory(_connection, _tenant),
            _tenant);
    }

    public void Dispose()
    {
        _seedDb.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task OrganizationOwner_CanReadOnlyInvoicesForCurrentOrganization()
    {
        var actor = Actor(_ownerA, _orgA.Id);

        Assert.True(await _service.CanViewAsync(actor, TestContext.Current.CancellationToken));
        var invoices = await _service.ListAsync(actor, ct: TestContext.Current.CancellationToken);

        var invoice = Assert.Single(invoices);
        Assert.Equal("PB-2026-000001", invoice.Number);
        Assert.Equal(_orgA.Id, invoice.OrgId);
        Assert.Equal(12500, invoice.AmountMinor);
        Assert.Equal("EUR", invoice.Currency);
    }

    [Fact]
    public async Task OrdinaryOrganizationMember_CannotReadInvoices()
    {
        var actor = Actor(_memberA, _orgA.Id);

        Assert.False(await _service.CanViewAsync(actor, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(actor, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InactiveOrganization_CannotReadInvoicesEvenForOwner()
    {
        _tenant.SetLifecycleStatus(OrganizationLifecycleStatus.Suspended, enforced: true);

        var actor = Actor(_ownerA, _orgA.Id);

        Assert.False(await _service.CanViewAsync(actor, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ListAsync(actor, ct: TestContext.Current.CancellationToken));
    }

    private void InsertInvoice(Guid orgId, string number)
    {
        var now = DateTimeOffset.UtcNow;
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "BillingInvoices"
                ("Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                 "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                 "ProviderKey", "ExternalInvoiceReference")
            VALUES
                (@id, @org_id, @number, @description, @amount, @currency,
                 @issued, @due, @status, NULL, NULL, NULL, NULL)
            """;
        Add(command, "id", DbType.Guid, Guid.NewGuid());
        Add(command, "org_id", DbType.Guid, orgId);
        Add(command, "number", DbType.String, number);
        Add(command, "description", DbType.String, "Manual invoice");
        Add(command, "amount", DbType.Int64, 12500L);
        Add(command, "currency", DbType.String, "EUR");
        Add(command, "issued", DbType.String, now.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "due", DbType.String, now.AddDays(14).ToString("O", CultureInfo.InvariantCulture));
        Add(command, "status", DbType.Int32, (int)BillingInvoiceStatus.Open);
        command.ExecuteNonQuery();
    }

    private static ActorContext Actor(User user, Guid orgId) =>
        new(
            ActorType.Human,
            user.Id,
            IsAdmin: false,
            TokenId: null,
            DeviceId: null,
            ProjectId: null,
            OrgId: orgId,
            Capabilities: ApiTokenCapability.None);

    private static void Add(SqliteCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
