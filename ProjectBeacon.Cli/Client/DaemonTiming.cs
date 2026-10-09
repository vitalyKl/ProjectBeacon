namespace ProjectBeacon.Cli.Client;

/// <summary>
/// Intervals and delay hook shared by the heartbeat, command, and chat loops. Tests replace them after the daemon is constructed.
/// </summary>
internal sealed class DaemonTiming
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandErrorDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ErrorBackoffInterval { get; set; } = TimeSpan.FromSeconds(3);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;
    // 250ms: streams parts as they arrive without hammering the local OpenCode serve.
    public TimeSpan ChatPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    // CPU inference routinely pauses between tokens; 10s of quiet counts the turn as done.
    public TimeSpan ChatIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);
    // Hard cap so a stuck generation cannot hold the command queue indefinitely.
    public TimeSpan ChatMaxDuration { get; set; } = TimeSpan.FromSeconds(180);
}
