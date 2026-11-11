namespace ProjectBeacon.OpsAgent;

using System.Collections.Concurrent;

public sealed class ReplayNonceStore
{
    private const int MaximumTrackedNonces = 8192;
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private long _lastCleanupUnixSeconds;

    public bool TryAccept(string nonce, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length != 32)
            return false;

        var nowUnix = now.ToUnixTimeSeconds();
        if (!_nonces.TryAdd(nonce, now.Add(ReplayWindow).ToUnixTimeSeconds()))
            return false;

        if (_nonces.Count > MaximumTrackedNonces / 2
            && Interlocked.Exchange(ref _lastCleanupUnixSeconds, nowUnix) < nowUnix - 10)
        {
            foreach (var item in _nonces)
            {
                if (item.Value < nowUnix)
                    _nonces.TryRemove(item.Key, out _);
            }
        }

        return _nonces.Count <= MaximumTrackedNonces;
    }
}
