namespace ProjectBeacon.Application.Billing;

using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Read-only customer billing view. Invoices are visible only to an active organization's
/// Owner or Admin; the organization id is taken from the server-bound tenant context and
/// repeated in SQL so EF filters and PostgreSQL RLS remain independent enforcement layers.
/// </summary>
public sealed class BillingInvoiceCustomerService(
    IBeaconDbFactory dbFactory,
    ITenantContext tenant)
{
    private const int MaximumListSize = 100;

    public async Task<bool> CanViewAsync(
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!TryGetOrganization(actor, out var orgId))
            return false;

        await using var db = dbFactory.CreateDbContext(tenant);
        return await db.OrgMembers
            .AsNoTracking()
            .AnyAsync(member =>
                member.OrgId == orgId
                && member.UserId == actor.UserId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin),
                ct);
    }

    public async Task<IReadOnlyList<BillingInvoice>> ListAsync(
        ActorContext actor,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaximumListSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaximumListSize}.");
        if (!TryGetOrganization(actor, out var orgId))
            throw new UnauthorizedAccessException("Billing is available only to owners and administrators of an active organization.");

        await using var db = dbFactory.CreateDbContext(tenant);
        var allowed = await db.OrgMembers
            .AsNoTracking()
            .AnyAsync(member =>
                member.OrgId == orgId
                && member.UserId == actor.UserId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin),
                ct);
        if (!allowed)
            throw new UnauthorizedAccessException("Billing is available only to owners and administrators of an active organization.");

        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                   "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                   "ProviderKey", "ExternalInvoiceReference"
            FROM "BillingInvoices"
            WHERE "OrgId" = @org_id
            ORDER BY "IssuedAtUtc" DESC, "Number" DESC
            LIMIT @limit
            """;
        Add(command, "org_id", DbType.Guid, orgId);
        Add(command, "limit", DbType.Int32, limit);

        var invoices = new List<BillingInvoice>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            invoices.Add(Read(reader));
        return invoices;
    }

    private bool TryGetOrganization(ActorContext actor, out Guid orgId)
    {
        orgId = Guid.Empty;
        if (!actor.IsHuman
            || actor.UserId is null
            || tenant.Unscoped
            || tenant.OrgId is not { } currentOrgId
            || currentOrgId == Guid.Empty
            || !tenant.LifecycleEnforced
            || tenant.LifecycleStatus != OrganizationLifecycleStatus.Active)
            return false;

        orgId = currentOrgId;
        return true;
    }

    private static BillingInvoice Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            ReadTimestamp(reader, 6),
            ReadTimestamp(reader, 7),
            (BillingInvoiceStatus)reader.GetInt16(8),
            reader.IsDBNull(9) ? null : ReadTimestamp(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(
                timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                    : timestamp).ToUniversalTime(),
            string text => DateTimeOffset.Parse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime(),
            _ => throw new InvalidDataException($"Unsupported invoice timestamp type at column {ordinal}.")
        };
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
