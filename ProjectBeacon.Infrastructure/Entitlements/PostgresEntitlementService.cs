namespace ProjectBeacon.Infrastructure.Entitlements;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
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

        await using var db = dbFactory.CreateDbContext();
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
        if (string.IsNullOrWhiteSpace(requirement.FeatureKey) || requirement.RequestedUnits < 1)
            return Result.Failure<EntitlementDecision>("A feature key and positive requested units are required.");

        if (orgId == Guid.Empty)
            return Result.Ok(new EntitlementDecision(false, "organization_not_found"));

        using (TenantScope.EnterUnscoped())
        {
            await using var lifecycleDb = dbFactory.CreateDbContext();
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
            return Result.Failure<EntitlementDecision>(result);
        if (result.Value is null)
            return Result.Ok(new EntitlementDecision(false, "entitlement_missing"));

        var grant = result.Value;
        if (!grant.Features.Contains(requirement.FeatureKey.Trim().ToLowerInvariant()))
            return Result.Ok(new EntitlementDecision(false, "feature_not_granted"));
        if (requirement.LimitKey is null)
            return Result.Ok(new EntitlementDecision(true));
        var key = requirement.LimitKey.Trim().ToLowerInvariant();
        if (!grant.Limits.TryGetValue(key, out var limit))
            return Result.Ok(new EntitlementDecision(false, "limit_not_granted"));
        return Result.Ok(new EntitlementDecision(
            requirement.RequestedUnits <= limit,
            requirement.RequestedUnits <= limit ? null : "limit_exceeded",
            null, limit));
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
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction.GetDbTransaction();
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
            Add(lockCommand, "lock_key", DbType.String, $"organization-entitlement:{request.OrgId:N}");
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

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
        await using var command = db.Database.GetDbConnection().CreateCommand();
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

    public Task<Result<OrganizationEntitlement>> ConvertAlphaToProductionAsync(
        ConvertAlphaToProductionRequest request, AdminActor actor, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.EntitlementsManage))
            return Task.FromResult(Result.Failure<OrganizationEntitlement>(
                "EntitlementsManage permission is required.", ErrorKind.Forbidden));
        return Task.FromResult(Result.Failure<OrganizationEntitlement>(
            "Alpha-to-production conversion is unavailable until subscription and billing persistence are implemented.",
            ErrorKind.Unavailable));
    }

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
