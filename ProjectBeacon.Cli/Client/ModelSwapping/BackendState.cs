namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Lifecycle of a single <see cref="IModelBackend"/>. Step 1 keeps transitions simple; the full
/// FSM (per-group semaphore, VRAM gate) is introduced in Step 2 on top of these states.
/// </summary>
public enum BackendState
{
    Idle,
    Starting,
    Ready,
    Stopping,
    Faulted
}
