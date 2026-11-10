namespace ProjectBeacon.Application.Billing;

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Billing;

public sealed record SaveBillingPlanRequest(
    string Key,
    string Name,
    BillingCadence Cadence,
    long? PriceMinor,
    string? Currency,
    IReadOnlyDictionary<string, long> Limits,
    IReadOnlyCollection<string> Features,
    bool IsActive);

/// <summary>
/// Operator-managed, provider-neutral catalog. Prices are optional during alpha; this service
/// does not create subscriptions, checkout sessions or provider-specific price references.
/// </summary>
public sealed class BillingPlanAdminService(
    IBeaconDbFactory dbFactory,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex PlanKeyPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProductKeyPattern = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const int MaximumKeys = 100;

    public async Task<IReadOnlyList<PlanDefinition>> ListAsync(
        AdminActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasPermission(OperatorPermission.BillingRead))
            throw new UnauthorizedAccessException("BillingRead permission is required.");

        var tenant = GlobalTenant();
        await using var db = dbFactory.CreateDbContext(tenant);
        var plans = await db.BillingPlans
            .AsNoTracking()
            .OrderBy(plan => plan.Key)
            .ToListAsync(ct);
        return plans.Select(ToContract).ToArray();
    }

    public async Task<PlanDefinition> SaveAsync(
        SaveBillingPlanRequest request,
        AdminActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasPermission(OperatorPermission.BillingManage))
            throw new UnauthorizedAccessException("BillingManage permission is required.");

        var now = timeProvider.GetUtcNow();
        if (!actor.IsElevatedAt(now))
            throw new UnauthorizedAccessException("Recent Control Plane authenticator verification is required.");

        var normalized = NormalizeAndValidate(request);
        var tenant = GlobalTenant();
        await using var db = dbFactory.CreateDbContext(tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var plan = await db.BillingPlans.SingleOrDefaultAsync(item => item.Key == normalized.Key, ct);
        var created = plan is null;
        if (plan is null)
        {
            plan = BillingPlan.Create(
                normalized.Key,
                normalized.Name,
                (short)normalized.Cadence,
                normalized.PriceMinor,
                normalized.Currency,
                JsonSerializer.Serialize(normalized.Limits, JsonOptions),
                JsonSerializer.Serialize(normalized.Features, JsonOptions),
                normalized.IsActive,
                now);
            db.BillingPlans.Add(plan);
        }
        else
        {
            plan.Update(
                normalized.Name,
                (short)normalized.Cadence,
                normalized.PriceMinor,
                normalized.Currency,
                JsonSerializer.Serialize(normalized.Limits, JsonOptions),
                JsonSerializer.Serialize(normalized.Features, JsonOptions),
                normalized.IsActive,
                now);
        }

        var audit = AuditEvent.Create(
            now.UtcDateTime,
            AuditActorKind.Human,
            actor.UserId,
            null,
            null,
            created ? "platform.billing_plan.created" : "platform.billing_plan.updated",
            "BillingPlan",
            normalized.Key,
            null,
            null,
            AuditOutcome.Success,
            detailsJson: created
                ? """{"resultCode":"plan_created"}"""
                : """{"resultCode":"plan_updated"}""");
        auditWriter.Stage(db, audit);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToContract(plan);
    }

    private static NormalizedPlan NormalizeAndValidate(SaveBillingPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key?.Trim() ?? string.Empty;
        if (!PlanKeyPattern.IsMatch(key))
            throw new ArgumentException("Plan key must be a lowercase slug of 1 to 64 characters.", nameof(request));
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 120)
            throw new ArgumentException("Plan name must contain 1 to 120 characters.", nameof(request));
        if (!Enum.IsDefined(request.Cadence))
            throw new ArgumentException("Plan cadence is invalid.", nameof(request));
        if (request.PriceMinor is < 0)
            throw new ArgumentException("Plan price must be a non-negative integer in minor currency units.", nameof(request));

        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? null
            : request.Currency.Trim().ToUpperInvariant();
        if ((request.PriceMinor is null) != (currency is null))
            throw new ArgumentException("Price and ISO 4217 currency must either both be configured or both be unset.", nameof(request));
        if (currency is not null
            && (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')))
            throw new ArgumentException("Currency must be a three-letter uppercase ISO 4217 code.", nameof(request));

        if (request.Features is null || request.Limits is null)
            throw new ArgumentException("Plan features and limits are required.", nameof(request));
        if (request.Features.Count is < 1 or > MaximumKeys || request.Limits.Count > MaximumKeys)
            throw new ArgumentException($"Plans support 1 to {MaximumKeys} features and up to {MaximumKeys} limit keys.", nameof(request));

        var features = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in request.Features)
        {
            var keyValue = NormalizeKey(raw);
            if (!ProductKeyPattern.IsMatch(keyValue) || !features.Add(keyValue))
                throw new ArgumentException("Feature keys must be valid and unique after normalization.", nameof(request));
        }
        if (!features.Contains("core"))
            throw new ArgumentException("Every plan must include the required 'core' feature.", nameof(request));

        var limits = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in request.Limits)
        {
            var limitKey = NormalizeKey(pair.Key);
            if (!ProductKeyPattern.IsMatch(limitKey) || pair.Value < 0 || !limits.TryAdd(limitKey, pair.Value))
                throw new ArgumentException("Limit keys must be valid and unique, and limit values must be non-negative.", nameof(request));
        }

        return new NormalizedPlan(
            key,
            name,
            request.Cadence,
            request.PriceMinor,
            currency,
            new Dictionary<string, long>(limits, StringComparer.Ordinal),
            features.ToArray(),
            request.IsActive);
    }

    private static string NormalizeKey(string? key) => (key ?? string.Empty).Trim().ToLowerInvariant();

    private static PlanDefinition ToContract(BillingPlan plan)
    {
        var limits = JsonSerializer.Deserialize<Dictionary<string, long>>(plan.LimitsJson, JsonOptions)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var features = JsonSerializer.Deserialize<string[]>(plan.FeaturesJson, JsonOptions) ?? [];
        return new PlanDefinition(
            plan.Key,
            plan.Name,
            (BillingCadence)plan.Cadence,
            plan.PriceMinor,
            plan.Currency,
            limits,
            new HashSet<string>(features, StringComparer.Ordinal),
            plan.IsActive);
    }

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }

    private sealed record NormalizedPlan(
        string Key,
        string Name,
        BillingCadence Cadence,
        long? PriceMinor,
        string? Currency,
        IReadOnlyDictionary<string, long> Limits,
        IReadOnlyCollection<string> Features,
        bool IsActive);
}
