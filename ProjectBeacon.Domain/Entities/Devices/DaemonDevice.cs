namespace ProjectBeacon.Domain.Entities.Devices;

using ProjectBeacon.Domain.Common;

/// <summary>
    /// A user's workstation daemon. User-owned and not tenant-filtered.
    /// Persists <see cref="TokenHash"/> and <see cref="TokenPrefix"/> only; the raw device token is never stored.
    /// </summary>
public class DaemonDevice : Entity
{
    /// <summary>Heartbeat age, in seconds, after which a non-revoked device is offline.</summary>
    public const int OnlineWindowSeconds = 45;

    public DaemonDevice() { }

    public string Name { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public string TokenPrefix { get; private set; } = string.Empty;
    public DateTime? LastHeartbeatAt { get; private set; }
    public string ProbeJson { get; private set; } = "{}";
    public string WorkstationJson { get; private set; } = "{}";
    public string DesiredWorkstationJson { get; private set; } = "{}";
    public long DesiredRevision { get; private set; }
    public long AppliedRevision { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>
    /// True when the device is not revoked and <paramref name="utcNow"/> is within <see cref="OnlineWindowSeconds"/> of the last heartbeat.
    /// </summary>
    public bool IsOnline(DateTime utcNow) =>
        !IsRevoked
        && LastHeartbeatAt is { } beat
        && utcNow - beat <= TimeSpan.FromSeconds(OnlineWindowSeconds);

    /// <summary>
    /// Creates an active device. <paramref name="tokenHash"/> and <paramref name="tokenPrefix"/> are stored as supplied.
    /// </summary>
    /// <exception cref="ArgumentException">A required argument is missing or white space, or <paramref name="userId"/> is empty.</exception>
    public static DaemonDevice Create(string name, Guid userId, string fingerprint, string tokenHash, string tokenPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPrefix);
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        var device = Entity.New<DaemonDevice>();
        device.Name = name.Trim();
        device.UserId = userId;
        device.Fingerprint = fingerprint.Trim();
        device.TokenHash = tokenHash;
        device.TokenPrefix = tokenPrefix;
        device.CreatedAt = DateTime.UtcNow;
        return device;
    }

    /// <summary>
    /// Replaces the stored token hash and prefix.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> or <paramref name="tokenPrefix"/> is null or white space.</exception>
    /// <exception cref="InvalidOperationException">The device is revoked.</exception>
    public void RotateToken(string tokenHash, string tokenPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPrefix);
        if (IsRevoked)
            throw new InvalidOperationException("Device is revoked.");
        TokenHash = tokenHash;
        TokenPrefix = tokenPrefix;
    }

    /// <summary>
    /// Records a heartbeat and, when supplied, replaces probe or workstation JSON.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is revoked.</exception>
    public void Heartbeat(string? probeJson = null, string? workstationJson = null)
    {
        if (IsRevoked)
            throw new InvalidOperationException("Device is revoked.");
        LastHeartbeatAt = DateTime.UtcNow;
        if (probeJson is not null)
            ProbeJson = probeJson;
        if (workstationJson is not null)
            WorkstationJson = workstationJson;
    }

    /// <summary>
    /// Renames the device.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or white space.</exception>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>
    /// Revokes the device. A second call leaves <see cref="RevokedAt"/> unchanged.
    /// </summary>
    public void Revoke()
    {
        RevokedAt ??= DateTime.UtcNow;
    }

    /// <summary>
    /// Increments <see cref="DesiredRevision"/>.
    /// </summary>
    public void BumpDesired() => DesiredRevision++;

    /// <summary>
    /// Stores desired workstation JSON, using <c>{}</c> when <paramref name="json"/> is null or white space, then bumps <see cref="DesiredRevision"/>.
    /// </summary>
    public void SetDesiredWorkstation(string json)
    {
        DesiredWorkstationJson = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        BumpDesired();
    }

    /// <summary>
    /// Advances <see cref="AppliedRevision"/> up to <see cref="DesiredRevision"/>. Negative values and revisions that do not move forward are ignored.
    /// </summary>
    public void MarkApplied(long revision)
    {
        if (revision < 0)
            return;
        if (revision > DesiredRevision)
            revision = DesiredRevision;
        if (revision > AppliedRevision)
            AppliedRevision = revision;
    }
}
