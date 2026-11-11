namespace ProjectBeacon.DockerLogProxy;

public sealed class ReplayNonceStore
{
    private const int MaximumTrackedNonces = 8192;
    private const long ReplayWindowSeconds = 60;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private long _lastCleanupUnixSeconds;

    public bool TryAccept(string nonce, DateTimeOffset now)
    {
        if (nonce is not { Length: 32 } || !nonce.All(Uri.IsHexDigit))
            return false;

        var nowUnixSeconds = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (_nonces.TryGetValue(nonce, out var expiresAtUnixSeconds))
            {
                if (expiresAtUnixSeconds >= nowUnixSeconds)
                    return false;

                _nonces.Remove(nonce);
            }

            if (_nonces.Count > MaximumTrackedNonces / 2
                && nowUnixSeconds - _lastCleanupUnixSeconds >= 10)
            {
                RemoveExpiredNonces(nowUnixSeconds);
                _lastCleanupUnixSeconds = nowUnixSeconds;
            }

            // Never insert and then reject: the cap must be a hard memory bound.
            if (_nonces.Count >= MaximumTrackedNonces)
            {
                RemoveExpiredNonces(nowUnixSeconds);
                _lastCleanupUnixSeconds = nowUnixSeconds;
                if (_nonces.Count >= MaximumTrackedNonces)
                    return false;
            }

            _nonces.Add(nonce, nowUnixSeconds + ReplayWindowSeconds);
            return true;
        }
    }

    private void RemoveExpiredNonces(long nowUnixSeconds)
    {
        foreach (var nonce in _nonces
                     .Where(item => item.Value < nowUnixSeconds)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _nonces.Remove(nonce);
        }
    }
}
