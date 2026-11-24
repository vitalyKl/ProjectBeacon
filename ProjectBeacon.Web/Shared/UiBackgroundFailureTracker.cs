namespace ProjectBeacon.Web.Shared;

/// <summary>
/// Suppresses transient background-operation errors until the configured number of
/// consecutive failures is reached. A successful poll clears the reported state.
/// </summary>
public sealed class UiBackgroundFailureTracker
{
    public const int DefaultFailureThreshold = 3;

    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private bool _reported;

    public UiBackgroundFailureTracker(int failureThreshold = DefaultFailureThreshold)
    {
        if (failureThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));

        _failureThreshold = failureThreshold;
    }

    /// <returns>true only once when the failure threshold is first reached.</returns>
    public bool RecordFailure()
    {
        if (_reported)
            return false;

        _consecutiveFailures++;
        if (_consecutiveFailures < _failureThreshold)
            return false;

        _reported = true;
        return true;
    }

    /// <returns>true when a previously reported degraded state has recovered.</returns>
    public bool RecordSuccess()
    {
        var recovered = _reported;
        _consecutiveFailures = 0;
        _reported = false;
        return recovered;
    }
}
