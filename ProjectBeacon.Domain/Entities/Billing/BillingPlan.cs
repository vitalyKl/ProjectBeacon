namespace ProjectBeacon.Domain.Entities.Billing;

using ProjectBeacon.Domain.Common;

/// <summary>
/// Provider-neutral plan configuration. A plan may be active before its price or currency
/// is configured; features and limits are ProjectBeacon-owned keys, not provider price IDs.
/// </summary>
public sealed class BillingPlan : Entity
{
    public string Key { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public short Cadence { get; private set; }
    public long? PriceMinor { get; private set; }
    public string? Currency { get; private set; }
    public string LimitsJson { get; private set; } = "{}";
    public string FeaturesJson { get; private set; } = "[\"core\"]";
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public BillingPlan()
    {
    }

    public static BillingPlan Create(
        string key,
        string name,
        short cadence,
        long? priceMinor,
        string? currency,
        string limitsJson,
        string featuresJson,
        bool isActive,
        DateTimeOffset nowUtc)
    {
        var plan = Entity.New<BillingPlan>();
        plan.Update(name, cadence, priceMinor, currency, limitsJson, featuresJson, isActive, nowUtc);
        plan.Key = ValidateKey(key);
        plan.CreatedAtUtc = nowUtc;
        return plan;
    }

    public void Update(
        string name,
        short cadence,
        long? priceMinor,
        string? currency,
        string limitsJson,
        string featuresJson,
        bool isActive,
        DateTimeOffset nowUtc)
    {
        if (Id == Guid.Empty)
            throw new InvalidOperationException("A billing plan must be created before it can be updated.");
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Billing plan timestamps must be UTC.", nameof(nowUtc));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new ArgumentException("Plan name must contain 1 to 120 characters.", nameof(name));
        if (cadence is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(cadence));
        if (priceMinor is < 0)
            throw new ArgumentOutOfRangeException(nameof(priceMinor));
        if ((priceMinor is null) != (currency is null))
            throw new ArgumentException("Price and currency must either both be configured or both be unset.");
        if (currency is not null
            && (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')))
            throw new ArgumentException("Currency must be a three-letter uppercase ISO 4217 code.", nameof(currency));
        if (string.IsNullOrWhiteSpace(limitsJson) || string.IsNullOrWhiteSpace(featuresJson))
            throw new ArgumentException("Plan limits and features JSON are required.");

        Name = name.Trim();
        Cadence = cadence;
        PriceMinor = priceMinor;
        Currency = currency;
        LimitsJson = limitsJson;
        FeaturesJson = featuresJson;
        IsActive = isActive;
        if (CreatedAtUtc == default)
            CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    private static string ValidateKey(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        var first = value.Length == 0 ? '\0' : value[0];
        var firstIsLetter = first is >= 'a' and <= 'z';
        var firstIsDigit = first is >= '0' and <= '9';
        if (value.Length is < 1 or > 64
            || (!firstIsLetter && !firstIsDigit)
            || value.Any(character => !(character is >= 'a' and <= 'z'
                or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("Plan key must be a lowercase slug of 1 to 64 characters.", nameof(key));
        return value;
    }
}
