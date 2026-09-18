namespace ProjectBeacon.Domain.Entities.Devices;

using ProjectBeacon.Domain.Common;

public class DaemonDevice : Entity
{
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
    public DateTime? RevokedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsOnline(DateTime utcNow) =>
        !IsRevoked
        && LastHeartbeatAt is { } beat
        && utcNow - beat <= TimeSpan.FromSeconds(OnlineWindowSeconds);

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

    public void RotateToken(string tokenHash, string tokenPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPrefix);
        if (IsRevoked)
            throw new InvalidOperationException("Device is revoked.");
        TokenHash = tokenHash;
        TokenPrefix = tokenPrefix;
    }

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

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void Revoke()
    {
        RevokedAt ??= DateTime.UtcNow;
    }
}
