namespace ProjectBeacon.Application.Operations;

using System.Text.RegularExpressions;
using ProjectBeacon.Application.Admin;

public enum PlatformServiceKind
{
    Web = 0,
    Api = 1,
    Worker = 2
}

public enum PlatformServiceHealth
{
    Healthy = 0,
    Stopping = 1
}

/// <summary>
/// A bounded, metadata-only heartbeat emitted by a first-party host process.
/// Instance identifiers are opaque; this contract must never carry hostnames, secrets, or log content.
/// </summary>
public sealed record PlatformServiceHeartbeatWrite(
    PlatformServiceKind Service,
    string InstanceId,
    string Version,
    string EnvironmentName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    PlatformServiceHealth Health)
{
    private static readonly Regex SafeInstanceId = new("^[A-Za-z0-9._:-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeVersion = new("^[A-Za-z0-9.+_-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeEnvironment = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Validate()
    {
        if (!Enum.IsDefined(Service))
            throw new ArgumentOutOfRangeException(nameof(Service));
        if (!Enum.IsDefined(Health))
            throw new ArgumentOutOfRangeException(nameof(Health));
        if (!SafeInstanceId.IsMatch(InstanceId ?? string.Empty))
            throw new ArgumentException("Instance id must be a short opaque identifier.", nameof(InstanceId));
        if (!SafeVersion.IsMatch(Version ?? string.Empty))
            throw new ArgumentException("Version must be a short machine-readable value.", nameof(Version));
        if (!SafeEnvironment.IsMatch(EnvironmentName ?? string.Empty))
            throw new ArgumentException("Environment must be a short machine-readable value.", nameof(EnvironmentName));
        if (StartedAtUtc.Offset != TimeSpan.Zero || HeartbeatAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Heartbeat timestamps must be UTC.");
        if (HeartbeatAtUtc < StartedAtUtc)
            throw new ArgumentException("Heartbeat cannot predate the process start.");
    }
}

public sealed record PlatformServiceInstance(
    PlatformServiceKind Service,
    string InstanceId,
    string Version,
    string EnvironmentName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastHeartbeatAtUtc,
    PlatformServiceHealth Health,
    bool IsStale);

public sealed record PlatformOperationsSnapshot(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<PlatformServiceInstance> Instances);

public interface IPlatformOperationsService
{
    Task RecordHeartbeatAsync(PlatformServiceHeartbeatWrite heartbeat, CancellationToken ct = default);

    Task<PlatformOperationsSnapshot> GetSnapshotAsync(AdminActor actor, CancellationToken ct = default);
}
