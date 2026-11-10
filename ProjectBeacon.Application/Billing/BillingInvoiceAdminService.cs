namespace ProjectBeacon.Application.Billing;

using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed record IssueBillingInvoiceRequest(
    Guid OrgId,
    string Description,
    long AmountMinor,
    string Currency,
    DateTimeOffset DueAtUtc);

/// <summary>
/// Operator-managed manual invoices. The application records invoice metadata and an
/// operator-attested external receipt reference only; it never collects payment credentials.
/// Every state change is committed with an append-only billing ledger entry and audit event.
/// </summary>
public sealed class BillingInvoiceAdminService(
    IBeaconDbFactory dbFactory,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ReferencePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const int MaximumListSize = 100;

    public async Task<IReadOnlyList<BillingInvoice>> ListAsync(
        AdminActor actor,
        Guid? orgId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequirePermission(actor, OperatorPermission.BillingRead);
        if (orgId == Guid.Empty)
            throw new ArgumentException("Organization id cannot be an empty GUID.", nameof(orgId));
        if (limit is < 1 or > MaximumListSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaximumListSize}.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                   "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                   "ProviderKey", "ExternalInvoiceReference"
            FROM "BillingInvoices"
            WHERE (@org_id IS NULL OR "OrgId" = @org_id)
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

    public async Task<BillingInvoice> IssueAsync(
        IssueBillingInvoiceRequest request,
        AdminActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireElevated(actor, timeProvider.GetUtcNow());
        var now = timeProvider.GetUtcNow();
        ValidateIssueRequest(request, now);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        await using (var orgLock = connection.CreateCommand())
        {
            orgLock.Transaction = dbTransaction;
            orgLock.CommandText = """
                SELECT "LifecycleStatus"
                FROM "Orgs"
                WHERE "Id" = @org_id
                FOR UPDATE
                """;
            Add(orgLock, "org_id", DbType.Guid, request.OrgId);
            var lifecycle = await orgLock.ExecuteScalarAsync(ct);
            if (lifecycle is null or DBNull)
                throw new KeyNotFoundException("Organization not found.");
            if (Convert.ToInt16(lifecycle, System.Globalization.CultureInfo.InvariantCulture)
                != (short)OrganizationLifecycleStatus.Active)
                throw new InvalidOperationException("An invoice can only be issued for an active organization.");
        }

        long sequence;
        await using (var numberCommand = connection.CreateCommand())
        {
            numberCommand.Transaction = dbTransaction;
            numberCommand.CommandText = """SELECT nextval('"BillingInvoiceNumberSequence"')""";
            sequence = Convert.ToInt64(await numberCommand.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var number = $"PB-{now:yyyy}-{sequence:D6}";
        var invoiceId = Guid.NewGuid();
        var description = request.Description.Trim();
        var currency = request.Currency.Trim().ToUpperInvariant();

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = dbTransaction;
            insert.CommandText = """
                INSERT INTO "BillingInvoices"
                    ("Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                     "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                     "ProviderKey", "ExternalInvoiceReference", "CreatedByUserId",
                     "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@id, @org_id, @number, @description, @amount, @currency,
                     @issued, @due, @status, NULL, NULL, NULL, NULL, @actor_id, @now, @now)
                """;
            Add(insert, "id", DbType.Guid, invoiceId);
            Add(insert, "org_id", DbType.Guid, request.OrgId);
            Add(insert, "number", DbType.String, number);
            Add(insert, "description", DbType.String, description);
            Add(insert, "amount", DbType.Int64, request.AmountMinor);
            Add(insert, "currency", DbType.String, currency);
            Add(insert, "issued", DbType.DateTimeOffset, now);
            Add(insert, "due", DbType.DateTimeOffset, request.DueAtUtc);
            Add(insert, "status", DbType.Int16, (short)BillingInvoiceStatus.Open);
            Add(insert, "actor_id", DbType.Guid, actor.UserId);
            Add(insert, "now", DbType.DateTimeOffset, now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await InsertLedgerAsync(
            connection, dbTransaction, request.OrgId, invoiceId,
            BillingLedgerEventType.InvoiceIssued, request.AmountMinor, currency,
            number, actor.UserId, "manual_invoice_issued", now, ct);

        auditWriter.Stage(db, Audit(actor, "organization.billing_invoice.issued",
            "BillingInvoice", invoiceId, request.OrgId, now,
            """{"newState":"Open","resultCode":"invoice_issued"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new BillingInvoice(
            invoiceId, request.OrgId, number, description, request.AmountMinor, currency,
            now, request.DueAtUtc, BillingInvoiceStatus.Open, null, null, null, null);
    }

    public async Task<BillingInvoice> MarkPaidAsync(
        Guid invoiceId,
        string paymentReference,
        AdminActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireElevated(actor, timeProvider.GetUtcNow());
        if (invoiceId == Guid.Empty)
            throw new ArgumentException("Invoice id is required.", nameof(invoiceId));
        if (!ReferencePattern.IsMatch(paymentReference ?? string.Empty))
            throw new ArgumentException(
                "Receipt reference must be 1 to 128 letters, digits, '.', '_', ':', '/' or '-' characters.",
                nameof(paymentReference));

        var reference = paymentReference.Trim();
        var now = timeProvider.GetUtcNow();
        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        var current = await GetForUpdateAsync(connection, dbTransaction, invoiceId, ct);

        if (current.Status == BillingInvoiceStatus.Paid)
        {
            if (string.Equals(current.PaymentReference, reference, StringComparison.Ordinal))
                return current;
            throw new InvalidOperationException("The invoice is already paid with a different receipt reference.");
        }
        if (current.Status != BillingInvoiceStatus.Open)
            throw new InvalidOperationException("Only an open invoice can be marked as paid.");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = dbTransaction;
            update.CommandText = """
                UPDATE "BillingInvoices"
                SET "Status" = @paid, "PaidAtUtc" = @paid_at,
                    "PaymentReference" = @reference, "UpdatedAtUtc" = @now
                WHERE "Id" = @id AND "Status" = @open
                """;
            Add(update, "paid", DbType.Int16, (short)BillingInvoiceStatus.Paid);
            Add(update, "paid_at", DbType.DateTimeOffset, now);
            Add(update, "reference", DbType.String, reference);
            Add(update, "now", DbType.DateTimeOffset, now);
            Add(update, "id", DbType.Guid, invoiceId);
            Add(update, "open", DbType.Int16, (short)BillingInvoiceStatus.Open);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Invoice state changed during payment recording.");
        }

        await InsertLedgerAsync(
            connection, dbTransaction, current.OrgId, invoiceId,
            BillingLedgerEventType.ManualMarkedPaid, current.AmountMinor, current.Currency,
            reference, actor.UserId, "manual_payment_recorded", now, ct);

        auditWriter.Stage(db, Audit(actor, "organization.billing_invoice.paid",
            "BillingInvoice", invoiceId, current.OrgId, now,
            """{"previousState":"Open","newState":"Paid","resultCode":"manual_payment_recorded"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return current with
        {
            Status = BillingInvoiceStatus.Paid,
            PaidAtUtc = now,
            PaymentReference = reference
        };
    }

    public Task<BillingInvoice> VoidAsync(
        Guid invoiceId, string reasonCode, AdminActor actor, CancellationToken ct = default) =>
        TransitionOpenInvoiceAsync(
            invoiceId, BillingInvoiceStatus.Void, BillingLedgerEventType.InvoiceVoided,
            "organization.billing_invoice.voided", "manual_invoice_voided",
            reasonCode, actor, ct);

    public Task<BillingInvoice> MarkUncollectibleAsync(
        Guid invoiceId, string reasonCode, AdminActor actor, CancellationToken ct = default) =>
        TransitionOpenInvoiceAsync(
            invoiceId, BillingInvoiceStatus.Uncollectible, BillingLedgerEventType.InvoiceMarkedUncollectible,
            "organization.billing_invoice.uncollectible", "invoice_marked_uncollectible",
            reasonCode, actor, ct);

    private async Task<BillingInvoice> TransitionOpenInvoiceAsync(
        Guid invoiceId,
        BillingInvoiceStatus targetStatus,
        BillingLedgerEventType eventType,
        string auditAction,
        string ledgerNote,
        string reasonCode,
        AdminActor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireElevated(actor, timeProvider.GetUtcNow());
        if (invoiceId == Guid.Empty)
            throw new ArgumentException("Invoice id is required.", nameof(invoiceId));
        if (!ReferencePattern.IsMatch(reasonCode ?? string.Empty))
            throw new ArgumentException("A short reason code is required.", nameof(reasonCode));

        var now = timeProvider.GetUtcNow();
        var reason = reasonCode.Trim();
        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        var current = await GetForUpdateAsync(connection, dbTransaction, invoiceId, ct);

        if (current.Status == targetStatus)
            return current;
        if (current.Status != BillingInvoiceStatus.Open)
            throw new InvalidOperationException("Only an open invoice can be moved to this terminal state.");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = dbTransaction;
            update.CommandText = """
                UPDATE "BillingInvoices"
                SET "Status" = @status, "UpdatedAtUtc" = @now
                WHERE "Id" = @id AND "Status" = @open
                """;
            Add(update, "status", DbType.Int16, (short)targetStatus);
            Add(update, "now", DbType.DateTimeOffset, now);
            Add(update, "id", DbType.Guid, invoiceId);
            Add(update, "open", DbType.Int16, (short)BillingInvoiceStatus.Open);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Invoice state changed during the transition.");
        }

        await InsertLedgerAsync(
            connection, dbTransaction, current.OrgId, invoiceId,
            eventType, current.AmountMinor, current.Currency,
            current.Number, actor.UserId, ledgerNote, now, ct);

        auditWriter.Stage(db, Audit(actor, auditAction,
            "BillingInvoice", invoiceId, current.OrgId, now,
            $$"""{"previousState":"Open","newState":"{{targetStatus}}","reasonCode":"{{reason}}" }"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return current with { Status = targetStatus };
    }

    private static async Task<BillingInvoice> GetForUpdateAsync(
        DbConnection connection, DbTransaction transaction, Guid invoiceId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                   "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                   "ProviderKey", "ExternalInvoiceReference"
            FROM "BillingInvoices"
            WHERE "Id" = @id
            FOR UPDATE
            """;
        Add(command, "id", DbType.Guid, invoiceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new KeyNotFoundException("Billing invoice not found.");
        return Read(reader);
    }

    private static async Task InsertLedgerAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid orgId,
        Guid invoiceId,
        BillingLedgerEventType eventType,
        long amountMinor,
        string currency,
        string reference,
        Guid actorUserId,
        string note,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "BillingLedgerEntries"
                ("Id", "OccurredAtUtc", "OrgId", "EventType", "Source",
                 "AmountMinor", "Currency", "ProviderKey", "ProviderEventId",
                 "Reference", "ActorUserId", "Note", "IdempotencyKey", "SubscriptionId", "InvoiceId")
            VALUES
                (@id, @occurred, @org_id, @event_type, @source,
                 @amount, @currency, NULL, NULL, @reference, @actor_id,
                 @note, NULL, NULL, @invoice_id)
            """;
        Add(command, "id", DbType.Guid, Guid.NewGuid());
        Add(command, "occurred", DbType.DateTimeOffset, occurredAt);
        Add(command, "org_id", DbType.Guid, orgId);
        Add(command, "event_type", DbType.Int16, (short)eventType);
        Add(command, "source", DbType.Int16, (short)SubscriptionSource.ManualInvoice);
        Add(command, "amount", DbType.Int64, amountMinor);
        Add(command, "currency", DbType.String, currency);
        Add(command, "reference", DbType.String, reference);
        Add(command, "actor_id", DbType.Guid, actorUserId);
        Add(command, "note", DbType.String, note);
        Add(command, "invoice_id", DbType.Guid, invoiceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static BillingInvoice Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            (BillingInvoiceStatus)reader.GetInt16(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    private static void ValidateIssueRequest(IssueBillingInvoiceRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrgId == Guid.Empty)
            throw new ArgumentException("Organization id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 200)
            throw new ArgumentException("Invoice description must contain 1 to 200 characters.", nameof(request));
        if (request.AmountMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Invoice amount must be a positive integer in minor units.");
        if (!CurrencyPattern.IsMatch(request.Currency?.Trim().ToUpperInvariant() ?? string.Empty))
            throw new ArgumentException("Currency must be a three-letter uppercase ISO 4217 code.", nameof(request));
        if (request.DueAtUtc.Offset != TimeSpan.Zero || request.DueAtUtc < now)
            throw new ArgumentException("Invoice due time must be a UTC timestamp no earlier than issue time.", nameof(request));
    }

    private static void RequirePermission(AdminActor actor, OperatorPermission permission)
    {
        if (!actor.HasPermission(permission))
            throw new UnauthorizedAccessException($"{permission} permission is required.");
    }

    private static void RequireElevated(AdminActor actor, DateTimeOffset now)
    {
        RequirePermission(actor, OperatorPermission.BillingManage);
        if (!actor.IsElevatedAt(now))
            throw new UnauthorizedAccessException("Recent Control Plane authenticator verification is required.");
    }

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static AuditEvent Audit(
        AdminActor actor, string action, string targetType, Guid targetId,
        Guid orgId, DateTimeOffset at, string detailsJson) =>
        AuditEvent.Create(at.UtcDateTime, AuditActorKind.Human, actor.UserId, null, null,
            action, targetType, targetId.ToString("D"), orgId, null, AuditOutcome.Success,
            detailsJson: detailsJson);
}
