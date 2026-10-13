namespace ProjectBeacon.Cli.Client;

/// <summary>
/// Per-process identity reported in heartbeats. A new value on every start lets the control
/// plane distinguish a restarted daemon from a still-running one, even while the device
/// stays online. Not persisted; it lives for the process lifetime.
/// </summary>
internal static class DaemonInstance
{
    public static readonly string Id = Guid.NewGuid().ToString("N");
}
