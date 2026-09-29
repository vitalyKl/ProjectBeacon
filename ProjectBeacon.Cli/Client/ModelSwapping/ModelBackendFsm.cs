namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Guarded state machine for one <see cref="IModelBackend"/>.
/// Transitions: Idle→Starting, Starting→Ready, Starting→Faulted, Ready→Stopping,
/// Ready→Faulted, Stopping→Idle, Faulted→Starting (retry), Faulted→Idle (give up).
/// </summary>
public sealed class ModelBackendFsm
{
    public BackendState Current { get; private set; } = BackendState.Idle;
    public string? LastError { get; private set; }

    public bool TryTransition(BackendState next, string? error = null)
    {
        if (!IsValid(Current, next))
            return false;
        Current = next;
        LastError = error;
        return true;
    }

    public void ForceReset()
    {
        Current = BackendState.Idle;
        LastError = null;
    }

    internal static bool IsValid(BackendState from, BackendState to) => (from, to) switch
    {
        (BackendState.Idle, BackendState.Starting) => true,
        (BackendState.Starting, BackendState.Ready) => true,
        (BackendState.Starting, BackendState.Faulted) => true,
        (BackendState.Ready, BackendState.Stopping) => true,
        (BackendState.Ready, BackendState.Faulted) => true,
        (BackendState.Stopping, BackendState.Idle) => true,
        (BackendState.Faulted, BackendState.Starting) => true,
        (BackendState.Faulted, BackendState.Idle) => true,
        _ => false
    };
}
