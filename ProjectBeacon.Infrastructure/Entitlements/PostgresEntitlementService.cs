namespace ProjectBeacon.Infrastructure.Entitlements;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Billing;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Entitlements;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class PostgresEntitlementService(
    IBeaconDbFactory dbFactory,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IEntitlementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<OrganizationEntitlement?>> GetEffectiveAsync(Guid orgId, CancellationToken ct = default)
    {
        if (orgId == Guid.Empty)
            return Result.Failure<OrganizationEntitlement?>("Organization id is required.");

        // Bind the connection to the requested organization explicitly. Do not inherit a
        // different organization from the current HTTP request or Blazor circuit.
        var tenant = new TenantContext();
        tenant.Assign(null, orgId, unscoped: false);
        await using var db = dbFactory.CreateDbContext(tenant);
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id", "OrgId", "Source", "Status", "StartsAtUtc", "EndsAtUtc",
                   "LimitsJson"::text, "FeaturesJson"::text, "SourceReference",
                   "GrantedByUserId", "Reason"
            FROM "OrganizationEntitlements"
            WHERE "OrgId" = @org_id
              AND "Status" IN (@active, @scheduled)
              AND "StartsAtUtc" <= @now
              AND ("EndsAtUtc" IS NULL OR "EndsAtUtc" > @now)
            ORDER BY "StartsAtUtc" DESC, "CreatedAtUtc" DESC
            LIMIT 1
            """;
        Add(command, "org_id", DbType.Guid, orgId);
        Add(command, "active", DbType.Int16, (short)EntitlementStatus.Active);
        Add(command, "scheduled", DbType.Int16, (short)EntitlementStatus.Scheduled);
        Add(command, "now", DbType.DateTimeOffset, timeProvider.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? Result.Ok<OrganizationEntitlement?>(Read(reader, timeProvider.GetUtcNow()))
            : Result.Ok<OrganizationEntitlement?>(null);
    }

    public async Task<Result<OrganizationEntitlement?>> GetForOperatorAsync(
        AdminActor actor, Guid orgId, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsRead))
            return Result.Failure<OrganizationEntitlement?>("EntitlementsRead permission is required.", ErrorKind.Forbidden);
        using var unscoped = TenantScope.EnterUnscoped();
        return await GetEffectiveAsync(orgId, ct);
    }

    public async Task<Result<IReadOnlyList<OrganizationEntitlement>>> ListForOperatorAsync(
        AdminActor actor, Guid orgId, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsRead))
            return Result.Failure<IReadOnlyList<OrganizationEntitlement>>("EntitlementsRead permission is required.", ErrorKind.Forbidden);
        if (orgId == Guid.Empty)
            return Result.Failure<IReadOnlyList<OrganizationEntitlement>>("Organization id is required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id", "OrgId", "Source", "Status", "StartsAtUtc", "EndsAtUtc",
                   "LimitsJson"::text, "FeaturesJson"::text, "SourceReference",
                   "GrantedByUserId", "Reason"
            FROM "OrganizationEntitlements"
            WHERE "OrgId" = @org_id
            ORDER BY "CreatedAtUtc" DESC
            LIMIT 50
            """;
        Add(command, "org_id", DbType.Guid, orgId);
        var now = timeProvider.GetUtcNow();
        var items = new List<OrganizationEntitlement>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(Read(reader, now));
        return Result.Ok<IReadOnlyList<OrganizationEntitlement>>(items);
    }

    public async Task<Result<EntitlementDecision>> EvaluateAsync(
        Guid orgId, EntitlementRequirement requirement, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requirement.FeatureKey)
            || (requirement.LimitKey is not null && string.IsNullOrWhiteSpace(requirement.LimitKey))
            || requirement.RequestedUnits < 1
            || requirement.CurrentUsage < 0)
            return Result.Failure<EntitlementDecision>("A feature key, valid optional limit key, non-negative current usage, and positive requested units are required.");

        if (orgId == Guid.Empty)
            return Result.Ok(new EntitlementDecision(false, "organization_not_found"));

        var lifecycleTenant = new TenantContext();
        lifecycleTenant.Assign(null, orgId, unscoped: false);
        await using (var lifecycleDb = dbFactory.CreateDbContext(lifecycleTenant))
        {
            var lifecycleStatus = await lifecycleDb.Orgs.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(org => org.Id == orgId)
                .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
                .FirstOrDefaultAsync(ct);

            if (lifecycleStatus is null)
                return Result.Ok(new EntitlementDecision(false, "organization_not_found"));
            if (lifecycleStatus != OrganizationLifecycleStatus.Active)
                return Result.Ok(new EntitlementDecision(false,
                    lifecycleStatus == OrganizationLifecycleStatus.Suspended
                        ? "organization_suspended"
                        : "organization_closed"));
        }

        var result = await GetEffectiveAsync(orgId, ct);
        if (!result.Success)
            return Result.Failure<EntitlementDecision>(result.Error ?? "Could not read the effective organization entitlement.");
        if (result.Value is null)
            return Result.Ok(new EntitlementDecision(false, "entitlement_missing"));

        var grant = result.Value;
        if (!grant.Features.Contains(requirement.FeatureKey.Trim().ToLowerInvariant()))
            return Result.Ok(new EntitlementDecision(false, "feature_not_granted", requirement.CurrentUsage));
        return Result.Ok(EntitlementLimitEvaluator.EvaluateCapacity(grant.Limits, requirement));
    }

    public async Task<Result<OrganizationEntitlement>> GrantAsync(
        GrantOrganizationEntitlementRequest request, AdminActor actor, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsManage))
            return Result.Failure<OrganizationEntitlement>("EntitlementsManage permission is required.", ErrorKind.Forbidden);
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Failure<OrganizationEntitlement>("Recent Control Plane authenticator verification is required.", ErrorKind.Forbidden);
        var now = timeProvider.GetUtcNow();
        var invalid = Validate(request, now);
        if (invalid is not null)
            return Result.Failure<OrganizationEntitlement>(invalid);
        var status = request.StartsAtUtc > now ? EntitlementStatus.Scheduled : EntitlementStatus.Active;
        var id = Guid.NewGuid();
        var features = request.Features.Select(NormalizeKey).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var limits = request.Limits.OrderBy(x => NormalizeKey(x.Key), StringComparer.Ordinal)
            .ToDictionary(x => NormalizeKey(x.Key), x => x.Value, StringComparer.Ordinal);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        if (!await db.Orgs.AnyAsync(org => org.Id == request.OrgId, ct))
            return Result.Failure<OrganizationEntitlement>("Organization not found.", ErrorKind.NotFound);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        await EntitlementCapacityLock.AcquireOrganizationAsync(
            db.Database, transaction, request.OrgId, ct);

        var replacedEntitlementIds = new List<Guid>();
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction.GetDbTransaction();
            revoke.CommandText = """
                UPDATE "OrganizationEntitlements"
                SET "Status" = @revoked, "UpdatedAtUtc" = @now
                WHERE "OrgId" = @org_id AND "Status" IN (@active, @scheduled)
                RETURNING "Id"
                """;
            Add(revoke, "revoked", DbType.Int16, (short)EntitlementStatus.Revoked);
            Add(revoke, "now", DbType.DateTimeOffset, now);
            Add(revoke, "org_id", DbType.Guid, request.OrgId);
            Add(revoke, "active", DbType.Int16, (short)EntitlementStatus.Active);
            Add(revoke, "scheduled", DbType.Int16, (short)EntitlementStatus.Scheduled);
            await using var reader = await revoke.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                replacedEntitlementIds.Add(reader.GetGuid(0));
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction.GetDbTransaction();
            insert.CommandText = """
                INSERT INTO "OrganizationEntitlements"
                    ("Id", "OrgId", "Source", "Status", "StartsAtUtc", "EndsAtUtc",
                     "LimitsJson", "FeaturesJson", "SourceReference", "GrantedByUserId",
                     "Reason", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@id, @org_id, @source, @status, @starts, @ends,
                     CAST(@limits AS jsonb), CAST(@features AS jsonb), NULL, @actor_id,
                     @reason, @now, @now)
                """;
            Add(insert, "id", DbType.Guid, id);
            Add(insert, "org_id", DbType.Guid, request.OrgId);
            Add(insert, "source", DbType.Int16, (short)MapSource(request.Source));
            Add(insert, "status", DbType.Int16, (short)status);
            Add(insert, "starts", DbType.DateTimeOffset, request.StartsAtUtc);
            Add(insert, "ends", DbType.DateTimeOffset, request.EndsAtUtc);
            Add(insert, "limits", DbType.String, JsonSerializer.Serialize(limits, JsonOptions));
            Add(insert, "features", DbType.String, JsonSerializer.Serialize(features, JsonOptions));
            Add(insert, "actor_id", DbType.Guid, actor.UserId);
            Add(insert, "reason", DbType.String, request.Reason.Trim());
            Add(insert, "now", DbType.DateTimeOffset, now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        foreach (var replacedId in replacedEntitlementIds)
        {
            auditWriter.Stage(db, Audit(actor, "organization.entitlement.replaced",
                "OrganizationEntitlement", replacedId, request.OrgId, now.UtcDateTime,
                """{"previousState":"ActiveOrScheduled","newState":"Revoked"}"""));
        }
        auditWriter.Stage(db, Audit(actor, "organization.entitlement.granted",
            "OrganizationEntitlement", id, request.OrgId, now.UtcDateTime,
            """{"newState":"Granted"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result.Ok(new OrganizationEntitlement(id, request.OrgId, MapSource(request.Source), status,
            request.StartsAtUtc, request.EndsAtUtc, limits,
            new HashSet<string>(features, StringComparer.Ordinal), null, actor.UserId, request.Reason.Trim()));
    }

    public async Task<Result> RevokeAsync(
        Guid entitlementId, string reason, AdminActor actor, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsManage))
            return Result.Forbidden("EntitlementsManage permission is required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (entitlementId == Guid.Empty)
            return Result.Failure("Entitlement id is required.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 80
            || reason.Trim().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')))
            return Result.Failure("Revoke reason must be a short code.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction.GetDbTransaction();
            lookup.CommandText = """
                SELECT "OrgId"
                FROM "OrganizationEntitlements"
                WHERE "Id" = @id AND "Status" IN (@active, @scheduled)
                """;
            Add(lookup, "id", DbType.Guid, entitlementId);
            Add(lookup, "active", DbType.Int16, (short)EntitlementStatus.Active);
            Add(lookup, "scheduled", DbType.Int16, (short)EntitlementStatus.Scheduled);
            var value = await lookup.ExecuteScalarAsync(ct);
            if (value is not Guid orgId)
                return Result.Failure("Active or scheduled entitlement not found.", ErrorKind.NotFound);

            await EntitlementCapacityLock.AcquireOrganizationAsync(
                db.Database, transaction, orgId, ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            UPDATE "OrganizationEntitlements"
            SET "Status" = @revoked, "UpdatedAtUtc" = @now
            WHERE "Id" = @id AND "Status" IN (@active, @scheduled)
            RETURNING "OrgId"
            """;
        var now = timeProvider.GetUtcNow();
        Add(command, "revoked", DbType.Int16, (short)EntitlementStatus.Revoked);
        Add(command, "now", DbType.DateTimeOffset, now);
        Add(command, "id", DbType.Guid, entitlementId);
        Add(command, "active", DbType.Int16, (short)EntitlementStatus.Active);
        Add(command, "scheduled", DbType.Int16, (short)EntitlementStatus.Scheduled);
        var orgIdValue = await command.ExecuteScalarAsync(ct);
        if (orgIdValue is not Guid orgId)
            return Result.Failure("Active or scheduled entitlement not found.", ErrorKind.NotFound);

        auditWriter.Stage(db, Audit(actor, "organization.entitlement.revoked",
            "OrganizationEntitlement", entitlementId, orgId, now.UtcDateTime,
            """{"previousState":"Active","newState":"Revoked"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    public async Task<Result<OrganizationEntitlement>> ConvertAlphaToProductionAsync(
        ConvertAlphaToProductionRequest request, AdminActor actor, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsManage))
            return Result.Failure<OrganizationEntitlement>(
                "EntitlementsManage permission is required.", ErrorKind.Forbidden);

        var now = timeProvider.GetUtcNow();
        if (!actor.IsElevatedAt(now))
            return Result.Failure<OrganizationEntitlement>(
                "Recent Control Plane authenticator verification is required.", ErrorKind.Forbidden);
        if (request is null || request.OrgId == Guid.Empty)
            return Result.Failure<OrganizationEntitlement>("Organization id is required.");
        if (string.IsNullOrWhiteSpace(request.PlanKey)
            || request.PlanKey.Length > 64
            || request.PlanKey != request.PlanKey.Trim()
            || request.PlanKey.Any(c => !(c is >= 'a' and <= 'z'
                or >= '0' and <= '9' or '.' or '_' or '-')))
            return Result.Failure<OrganizationEntitlement>("A valid product plan key is required.");
        if (!SafeBillingReference(request.BillingReference, 128))
            return Result.Failure<OrganizationEntitlement>(
                "A manual invoice or receipt reference is required.");
        if (!SafeIdempotencyKey(request.IdempotencyKey, 128))
            return Result.Failure<OrganizationEntitlement>(
                "A valid idempotency key containing only letters, digits, '.', '_', ':' or '-' is required.");
        if (request.StartsAtUtc.Offset != TimeSpan.Zero
            || (request.EndsAtUtc is { } suppliedEnd && suppliedEnd.Offset != TimeSpan.Zero))
            return Result.Failure<OrganizationEntitlement>("Subscription period timestamps must be UTC.");

        // PostgreSQL timestamptz stores microsecond precision. Normalize before both persistence
        // and idempotency comparison so an exact replay does not fail on discarded 100-ns ticks.
        request = request with
        {
            StartsAtUtc = TruncateToPostgresPrecision(request.StartsAtUtc),
            EndsAtUtc = request.EndsAtUtc is { } periodEnd
                ? TruncateToPostgresPrecision(periodEnd)
                : null
        };
        if (request.StartsAtUtc > now)
            return Result.Failure<OrganizationEntitlement>(
                "Alpha-to-production conversion must start immediately; future-dated conversion is not supported.");
        if (request.EndsAtUtc is { } ends
            && (ends <= request.StartsAtUtc || ends <= now))
            return Result.Failure<OrganizationEntitlement>("Subscription period end must be after the current time.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return Result.Failure<OrganizationEntitlement>("A conversion reason of 1 to 500 characters is required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        // Lock the organization lifecycle row before changing its commercial state.
        await using (var lifecycleLock = connection.CreateCommand())
        {
            lifecycleLock.Transaction = dbTransaction;
            lifecycleLock.CommandText = """
                SELECT "LifecycleStatus"
                FROM "Orgs"
                WHERE "Id" = @org_id
                FOR UPDATE
                """;
            Add(lifecycleLock, "org_id", DbType.Guid, request.OrgId);
            var lifecycle = await lifecycleLock.ExecuteScalarAsync(ct);
            if (lifecycle is null || lifecycle is DBNull)
                return Result.Failure<OrganizationEntitlement>("Organization not found.", ErrorKind.NotFound);
            if (Convert.ToInt16(lifecycle, System.Globalization.CultureInfo.InvariantCulture)
                != (short)OrganizationLifecycleStatus.Active)
                return Result.Failure<OrganizationEntitlement>(
                    "Only an active organization can be converted to production.", ErrorKind.Forbidden);
        }

        // Serialize entitlement replacement with project/member capacity consumption.
        await EntitlementCapacityLock.AcquireOrganizationAsync(
            db.Database, transaction, request.OrgId, ct);

        // Billing has a separate lock for billing lifecycle operations on the same org.
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = dbTransaction;
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
            Add(lockCommand, "lock_key", DbType.String, $"organization-billing:{request.OrgId:N}");
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        // Idempotency keys are global, not organization-local. This second lock ensures
        // that simultaneous use of the same key by two organizations is returned as a
        // deterministic conflict instead of racing into the unique index.
        await using (var idempotencyLock = connection.CreateCommand())
        {
            idempotencyLock.Transaction = dbTransaction;
            idempotencyLock.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
            Add(idempotencyLock, "lock_key", DbType.String,
                $"billing-idempotency:{request.IdempotencyKey.Trim()}");
            await idempotencyLock.ExecuteNonQueryAsync(ct);
        }

        // Idempotent replay returns the original entitlement. Reusing a key for another
        // organization, plan, period or billing reference is rejected without side effects.
        Guid? replaySubscriptionId = null;
        var replayMatches = false;
        await using (var replay = connection.CreateCommand())
        {
            replay.Transaction = dbTransaction;
            replay.CommandText = """
                SELECT e."OrgId", e."EventType", e."Reference", s."PlanKey",
                       s."CurrentPeriodStartsAtUtc", s."CurrentPeriodEndsAtUtc", s."Id"
                FROM "BillingLedgerEntries" e
                LEFT JOIN "OrganizationSubscriptions" s ON s."Id" = e."SubscriptionId"
                WHERE e."IdempotencyKey" = @idempotency_key
                """;
            Add(replay, "idempotency_key", DbType.String, request.IdempotencyKey.Trim());
            await using var reader = await replay.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                replayMatches =
                    reader.GetGuid(0) == request.OrgId
                    && reader.GetInt16(1) == (short)BillingLedgerEventType.SubscriptionActivated
                    && !reader.IsDBNull(2)
                    && string.Equals(reader.GetString(2), request.BillingReference.Trim(), StringComparison.Ordinal)
                    && !reader.IsDBNull(3)
                    && string.Equals(reader.GetString(3), request.PlanKey, StringComparison.Ordinal)
                    && reader.GetFieldValue<DateTimeOffset>(4) == request.StartsAtUtc
                    && (reader.IsDBNull(5)
                        ? request.EndsAtUtc is null
                        : request.EndsAtUtc is { } requestedEnd
                            && reader.GetFieldValue<DateTimeOffset>(5) == requestedEnd)
                    && !reader.IsDBNull(6);
                if (replayMatches)
                    replaySubscriptionId = reader.GetGuid(6);
            }
        }

        if (replaySubscriptionId is { } existingSubscriptionId)
        {
            var existingEntitlementId = await FindConversionEntitlementIdAsync(
                connection, dbTransaction, existingSubscriptionId, request.OrgId, ct);
            if (existingEntitlementId is null)
                return Result.Failure<OrganizationEntitlement>(
                    "The prior conversion ledger entry exists but its entitlement record is missing.",
                    ErrorKind.Unavailable);

            await using var existing = connection.CreateCommand();
            existing.Transaction = dbTransaction;
            existing.CommandText = """
                SELECT "Id", "OrgId", "Source", "Status", "StartsAtUtc", "EndsAtUtc",
                       "LimitsJson"::text, "FeaturesJson"::text, "SourceReference",
                       "GrantedByUserId", "Reason"
                FROM "OrganizationEntitlements"
                WHERE "Id" = @id
                """;
            Add(existing, "id", DbType.Guid, existingEntitlementId.Value);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Result.Failure<OrganizationEntitlement>(
                    "The prior conversion ledger entry exists but its entitlement record is missing.",
                    ErrorKind.Unavailable);
            return Result.Ok(Read(reader, now));
        }

        // A key used by a different event type or with different request data is never reused.
        // A matching idempotency row without a matching subscription is also a hard conflict.
        await using (var reusedKey = connection.CreateCommand())
        {
            reusedKey.Transaction = dbTransaction;
            reusedKey.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM "BillingLedgerEntries"
                    WHERE "IdempotencyKey" = @idempotency_key
                )
                """;
            Add(reusedKey, "idempotency_key", DbType.String, request.IdempotencyKey.Trim());
            if (Convert.ToBoolean(await reusedKey.ExecuteScalarAsync(ct),
                    System.Globalization.CultureInfo.InvariantCulture))
                return Result.Failure<OrganizationEntitlement>(
                    "The idempotency key has already been used for a different billing operation.",
                    ErrorKind.Validation);
        }

        var plan = await db.BillingPlans.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == request.PlanKey, ct);
        if (plan is null || !plan.IsActive)
            return Result.Failure<OrganizationEntitlement>(
                "The selected billing plan does not exist or is inactive.", ErrorKind.NotFound);

        if ((plan.Cadence == (short)BillingCadence.Monthly
                || plan.Cadence == (short)BillingCadence.Annual)
            && request.EndsAtUtc is null)
            return Result.Failure<OrganizationEntitlement>(
                "Monthly and annual subscriptions require an explicit current-period end.");
        if (plan.Cadence < (short)BillingCadence.OneTime
            || plan.Cadence > (short)BillingCadence.Annual)
            return Result.Failure<OrganizationEntitlement>("The selected billing plan has an invalid cadence.");

        Dictionary<string, long> limits;
        string[] features;
        try
        {
            limits = JsonSerializer.Deserialize<Dictionary<string, long>>(plan.LimitsJson, JsonOptions)
                ?? throw new JsonException("Plan limits must be a JSON object.");
            features = JsonSerializer.Deserialize<string[]>(plan.FeaturesJson, JsonOptions)
                ?? throw new JsonException("Plan features must be a JSON array.");
        }
        catch (JsonException)
        {
            return Result.Failure<OrganizationEntitlement>(
                "The selected billing plan contains invalid product limits or features.");
        }

        if (limits.Count > 100 || features.Length is < 1 or > 100
            || limits.Any(item => !ValidKey(item.Key) || item.Value < 0)
            || features.Any(feature => !ValidKey(feature))
            || features.Select(NormalizeKey).Distinct(StringComparer.Ordinal).Count() != features.Length
            || !features.Any(feature => NormalizeKey(feature) == EntitlementFeatureKeys.Core))
            return Result.Failure<OrganizationEntitlement>(
                "The selected billing plan does not contain a valid product entitlement.");

        // Only the currently effective AlphaProgram grant can be converted. Other grant
        // sources are never silently revoked by a billing operation.
        Guid alphaEntitlementId;
        await using (var alpha = connection.CreateCommand())
        {
            alpha.Transaction = dbTransaction;
            alpha.CommandText = """
                SELECT "Id"
                FROM "OrganizationEntitlements"
                WHERE "OrgId" = @org_id
                  AND "Source" = @alpha_source
                  AND "Status" = @active_status
                  AND "StartsAtUtc" <= @now
                  AND ("EndsAtUtc" IS NULL OR "EndsAtUtc" > @now)
                FOR UPDATE
                """;
            Add(alpha, "org_id", DbType.Guid, request.OrgId);
            Add(alpha, "alpha_source", DbType.Int16, (short)EntitlementSource.AlphaProgram);
            Add(alpha, "active_status", DbType.Int16, (short)EntitlementStatus.Active);
            Add(alpha, "now", DbType.DateTimeOffset, now);
            var value = await alpha.ExecuteScalarAsync(ct);
            if (value is not Guid id)
                return Result.Failure<OrganizationEntitlement>(
                    "The organization has no currently effective AlphaProgram entitlement.",
                    ErrorKind.Validation);
            alphaEntitlementId = id;
        }

        await using (var activeSubscription = connection.CreateCommand())
        {
            activeSubscription.Transaction = dbTransaction;
            activeSubscription.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM "OrganizationSubscriptions"
                    WHERE "OrgId" = @org_id AND "Status" IN (0, 1, 2, 3)
                )
                """;
            Add(activeSubscription, "org_id", DbType.Guid, request.OrgId);
            if (Convert.ToBoolean(await activeSubscription.ExecuteScalarAsync(ct),
                    System.Globalization.CultureInfo.InvariantCulture))
                return Result.Failure<OrganizationEntitlement>(
                    "The organization already has a non-terminal subscription.", ErrorKind.Validation);
        }

        var subscriptionId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var normalizedFeatures = features.Select(NormalizeKey)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var normalizedLimits = limits
            .OrderBy(item => NormalizeKey(item.Key), StringComparer.Ordinal)
            .ToDictionary(item => NormalizeKey(item.Key), item => item.Value, StringComparer.Ordinal);
        var sourceReference = subscriptionId.ToString("D");

        await using (var insertSubscription = connection.CreateCommand())
        {
            insertSubscription.Transaction = dbTransaction;
            insertSubscription.CommandText = """
                INSERT INTO "OrganizationSubscriptions"
                    ("Id", "OrgId", "PlanKey", "Status", "Source", "TrialEndsAtUtc",
                     "CurrentPeriodStartsAtUtc", "CurrentPeriodEndsAtUtc", "CancelAtPeriodEnd",
                     "GraceUntilUtc", "ProviderKey", "ExternalCustomerReference",
                     "ExternalSubscriptionReference", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@id, @org_id, @plan_key, @status, @source, NULL,
                     @period_starts, @period_ends, FALSE, NULL, NULL, NULL, NULL, @now, @now)
                """;
            Add(insertSubscription, "id", DbType.Guid, subscriptionId);
            Add(insertSubscription, "org_id", DbType.Guid, request.OrgId);
            Add(insertSubscription, "plan_key", DbType.String, plan.Key);
            Add(insertSubscription, "status", DbType.Int16, (short)SubscriptionStatus.Active);
            Add(insertSubscription, "source", DbType.Int16, (short)SubscriptionSource.ManualInvoice);
            Add(insertSubscription, "period_starts", DbType.DateTimeOffset, request.StartsAtUtc);
            Add(insertSubscription, "period_ends", DbType.DateTimeOffset, request.EndsAtUtc);
            Add(insertSubscription, "now", DbType.DateTimeOffset, now);
            await insertSubscription.ExecuteNonQueryAsync(ct);
        }

        await using (var revokeAlpha = connection.CreateCommand())
        {
            revokeAlpha.Transaction = dbTransaction;
            revokeAlpha.CommandText = """
                UPDATE "OrganizationEntitlements"
                SET "Status" = @revoked, "UpdatedAtUtc" = @now
                WHERE "Id" = @alpha_id AND "Status" = @active
                """;
            Add(revokeAlpha, "revoked", DbType.Int16, (short)EntitlementStatus.Revoked);
            Add(revokeAlpha, "now", DbType.DateTimeOffset, now);
            Add(revokeAlpha, "alpha_id", DbType.Guid, alphaEntitlementId);
            Add(revokeAlpha, "active", DbType.Int16, (short)EntitlementStatus.Active);
            if (await revokeAlpha.ExecuteNonQueryAsync(ct) != 1)
                return Result.Failure<OrganizationEntitlement>(
                    "The Alpha entitlement changed during conversion. No production access was activated.",
                    ErrorKind.Validation);
        }

        await using (var insertEntitlement = connection.CreateCommand())
        {
            insertEntitlement.Transaction = dbTransaction;
            insertEntitlement.CommandText = """
                INSERT INTO "OrganizationEntitlements"
                    ("Id", "OrgId", "Source", "Status", "StartsAtUtc", "EndsAtUtc",
                     "LimitsJson", "FeaturesJson", "SourceReference", "GrantedByUserId",
                     "Reason", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@id, @org_id, @source, @status, @starts, @ends,
                     CAST(@limits AS jsonb), CAST(@features AS jsonb), @source_reference,
                     @actor_id, @reason, @now, @now)
                """;
            Add(insertEntitlement, "id", DbType.Guid, entitlementId);
            Add(insertEntitlement, "org_id", DbType.Guid, request.OrgId);
            Add(insertEntitlement, "source", DbType.Int16, (short)EntitlementSource.Subscription);
            Add(insertEntitlement, "status", DbType.Int16, (short)EntitlementStatus.Active);
            Add(insertEntitlement, "starts", DbType.DateTimeOffset, request.StartsAtUtc);
            Add(insertEntitlement, "ends", DbType.DateTimeOffset, request.EndsAtUtc);
            Add(insertEntitlement, "limits", DbType.String, JsonSerializer.Serialize(normalizedLimits, JsonOptions));
            Add(insertEntitlement, "features", DbType.String, JsonSerializer.Serialize(normalizedFeatures, JsonOptions));
            Add(insertEntitlement, "source_reference", DbType.String, sourceReference);
            Add(insertEntitlement, "actor_id", DbType.Guid, actor.UserId);
            Add(insertEntitlement, "reason", DbType.String, request.Reason.Trim());
            Add(insertEntitlement, "now", DbType.DateTimeOffset, now);
            await insertEntitlement.ExecuteNonQueryAsync(ct);
        }

        await using (var insertEvent = connection.CreateCommand())
        {
            insertEvent.Transaction = dbTransaction;
            insertEvent.CommandText = """
                INSERT INTO "BillingLedgerEntries"
                    ("Id", "OccurredAtUtc", "OrgId", "EventType", "Source",
                     "AmountMinor", "Currency", "ProviderKey", "ProviderEventId",
                     "Reference", "ActorUserId", "Note", "IdempotencyKey", "SubscriptionId")
                VALUES
                    (@id, @occurred, @org_id, @event_type, @source,
                     NULL, NULL, NULL, NULL, @reference, @actor_id,
                     'alpha_to_production', @idempotency_key, @subscription_id)
                """;
            Add(insertEvent, "id", DbType.Guid, Guid.NewGuid());
            Add(insertEvent, "occurred", DbType.DateTimeOffset, now);
            Add(insertEvent, "org_id", DbType.Guid, request.OrgId);
            Add(insertEvent, "event_type", DbType.Int16, (short)BillingLedgerEventType.SubscriptionActivated);
            Add(insertEvent, "source", DbType.Int16, (short)SubscriptionSource.ManualInvoice);
            Add(insertEvent, "reference", DbType.String, request.BillingReference.Trim());
            Add(insertEvent, "actor_id", DbType.Guid, actor.UserId);
            Add(insertEvent, "idempotency_key", DbType.String, request.IdempotencyKey.Trim());
            Add(insertEvent, "subscription_id", DbType.Guid, subscriptionId);
            await insertEvent.ExecuteNonQueryAsync(ct);
        }

        auditWriter.Stage(db, Audit(actor, "organization.entitlement.replaced",
            "OrganizationEntitlement", alphaEntitlementId, request.OrgId, now.UtcDateTime,
            """{"previousState":"AlphaProgram","newState":"Revoked"}"""));
        auditWriter.Stage(db, Audit(actor, "organization.entitlement.converted",
            "OrganizationEntitlement", entitlementId, request.OrgId, now.UtcDateTime,
            """{"newState":"Subscription","source":"ManualInvoice"}"""));
        auditWriter.Stage(db, Audit(actor, "organization.subscription.activated",
            "OrganizationSubscription", subscriptionId, request.OrgId, now.UtcDateTime,
            """{"newState":"Active","source":"ManualInvoice"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result.Ok(new OrganizationEntitlement(
            entitlementId,
            request.OrgId,
            EntitlementSource.Subscription,
            EntitlementStatus.Active,
            request.StartsAtUtc,
            request.EndsAtUtc,
            normalizedLimits,
            new HashSet<string>(normalizedFeatures, StringComparer.Ordinal),
            sourceReference,
            actor.UserId,
            request.Reason.Trim()));
    }

    private static async Task<Guid?> FindConversionEntitlementIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid subscriptionId,
        Guid orgId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Id" FROM "OrganizationEntitlements"
            WHERE "Source" = @source AND "SourceReference" = @source_reference
              AND "OrgId" = @org_id
            ORDER BY "CreatedAtUtc" DESC
            LIMIT 1
            """;
        Add(command, "source", DbType.Int16, (short)EntitlementSource.Subscription);
        Add(command, "source_reference", DbType.String, subscriptionId.ToString("D"));
        Add(command, "org_id", DbType.Guid, orgId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is Guid id ? id : null;
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        var utcTicks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    private static bool SafeBillingReference(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value == value.Trim()
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-' or '/');

    private static bool SafeIdempotencyKey(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value == value.Trim()
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');


    private static string? Validate(GrantOrganizationEntitlementRequest request, DateTimeOffset now)
    {
        if (request.Features is null || request.Limits is null)
            return "Feature and limit collections are required.";
        if (request.OrgId == Guid.Empty) return "Organization id is required.";
        if (!Enum.IsDefined(request.Source)) return "Entitlement source is invalid.";
        if (request.StartsAtUtc.Offset != TimeSpan.Zero
            || (request.EndsAtUtc.HasValue && request.EndsAtUtc.Value.Offset != TimeSpan.Zero))
            return "Entitlement timestamps must be UTC.";
        if (request.EndsAtUtc is { } ends && ends <= request.StartsAtUtc)
            return "Entitlement end must be after its start.";
        if (request.EndsAtUtc is { } end && end <= now)
            return "Entitlement end must be in the future.";
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return "A reason of 1 to 500 characters is required.";
        if (!request.Features.Any(key =>
                string.Equals(NormalizeKey(key), EntitlementFeatureKeys.Core, StringComparison.Ordinal)))
            return "Every organization entitlement must include the required 'core' feature.";
        if (request.Features.Count > 100 || request.Limits.Count > 100)
            return "At most 100 feature and limit keys may be granted.";
        if (request.Features.Any(key => !ValidKey(key))
            || request.Limits.Any(x => !ValidKey(x.Key) || x.Value < 0))
            return "Feature and limit keys must be product keys; limits must be non-negative.";
        if (request.Features.Select(NormalizeKey).Distinct(StringComparer.Ordinal).Count() != request.Features.Count
            || request.Limits.Keys.Select(NormalizeKey).Distinct(StringComparer.Ordinal).Count() != request.Limits.Count)
            return "Feature and limit keys must be unique after normalization.";
        return null;
    }

    private static bool ValidKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && value == value.Trim()
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();

    private static EntitlementSource MapSource(OperatorEntitlementSource source) => source switch
    {
        OperatorEntitlementSource.AlphaProgram => EntitlementSource.AlphaProgram,
        OperatorEntitlementSource.Complimentary => EntitlementSource.Complimentary,
        OperatorEntitlementSource.OperatorOverride => EntitlementSource.OperatorOverride,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static OrganizationEntitlement Read(DbDataReader reader, DateTimeOffset now)
    {
        var starts = reader.GetFieldValue<DateTimeOffset>(4);
        DateTimeOffset? ends = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
        var status = (EntitlementStatus)reader.GetInt16(3);
        if (status == EntitlementStatus.Scheduled && starts <= now && (ends is null || ends > now))
            status = EntitlementStatus.Active;
        else if (status == EntitlementStatus.Active && ends is { } expiry && expiry <= now)
            status = EntitlementStatus.Expired;

        var limits = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(6), JsonOptions)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var features = JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [];
        return new OrganizationEntitlement(
            reader.GetGuid(0), reader.GetGuid(1),
            (EntitlementSource)reader.GetInt16(2), status, starts, ends,
            limits, new HashSet<string>(features, StringComparer.Ordinal),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
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
        Guid orgId, DateTime atUtc, string detailsJson) =>
        AuditEvent.Create(atUtc, AuditActorKind.Human, actor.UserId, null, null,
            action, targetType, targetId.ToString(), orgId, null, AuditOutcome.Success,
            detailsJson: detailsJson);
}
