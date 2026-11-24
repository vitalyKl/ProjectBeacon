namespace ProjectBeacon.Cli.Client;

using System.Text.Json;

internal sealed record GitAutoPullEvent(DateTimeOffset At, string FromSha, string ToSha, int Commits);
internal sealed record GitAutoSyncError(string Code, DateTimeOffset At);
internal sealed record GitAutoSyncProjectState(
    Guid ProjectId,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool Clean,
    bool Detached,
    bool HasUpstream,
    bool Diverged,
    DateTimeOffset CheckedAt,
    GitAutoPullEvent? LastAutoPull,
    GitAutoSyncError? LastError,
    string? AutoPullBlockedReason);

/// <summary>Thread-safe read model shared by the background loop and heartbeat publisher.</summary>
internal sealed class GitAutoSyncSnapshot
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, GitAutoSyncProjectState> _projects = [];

    public bool Set(GitAutoSyncProjectState value)
    {
        lock (_sync)
        {
            if (_projects.TryGetValue(value.ProjectId, out var current))
            {
                // Refresh checkedAt/error timestamps in the heartbeat without logging a
                // false "state change" on every successful interval or repeated outage.
                var comparable = current with { CheckedAt = value.CheckedAt, LastError = value.LastError };
                if (JsonSerializer.Serialize(comparable) == JsonSerializer.Serialize(value))
                {
                    _projects[value.ProjectId] = value;
                    return false;
                }
            }
            _projects[value.ProjectId] = value;
            return true;
        }
    }

    public void KeepProjects(IReadOnlySet<Guid> projectIds)
    {
        lock (_sync)
        {
            foreach (var id in _projects.Keys.Where(id => !projectIds.Contains(id)).ToArray())
                _projects.Remove(id);
        }
    }

    public GitAutoSyncProjectState? Get(Guid projectId)
    {
        lock (_sync)
            return _projects.GetValueOrDefault(projectId);
    }

    public object ToProbeValue()
    {
        lock (_sync)
        {
            return _projects.Values.OrderBy(value => value.ProjectId).Select(value =>
                (object)new Dictionary<string, object?>
                {
                    ["projectId"] = value.ProjectId,
                    ["branch"] = value.Branch,
                    ["upstream"] = value.Upstream,
                    ["ahead"] = value.Ahead,
                    ["behind"] = value.Behind,
                    ["clean"] = value.Clean,
                    ["detached"] = value.Detached,
                    ["hasUpstream"] = value.HasUpstream,
                    ["diverged"] = value.Diverged,
                    ["checkedAt"] = value.CheckedAt,
                    ["lastAutoPull"] = value.LastAutoPull is null ? null : new Dictionary<string, object?>
                    {
                        ["at"] = value.LastAutoPull.At,
                        ["fromSha"] = value.LastAutoPull.FromSha,
                        ["toSha"] = value.LastAutoPull.ToSha,
                        ["commits"] = value.LastAutoPull.Commits
                    },
                    ["lastError"] = value.LastError is null ? null : new Dictionary<string, object?>
                    {
                        ["code"] = value.LastError.Code,
                        ["at"] = value.LastError.At
                    },
                    ["autoPullBlockedReason"] = value.AutoPullBlockedReason
                }).ToArray();
        }
    }
}
