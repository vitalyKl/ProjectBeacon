namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Devices;

internal sealed class GitAutoSyncLoop : IDaemonLoop
{
    private sealed record BackoffState(int Failures, DateTimeOffset RetryAt);

    private readonly HttpClient _http;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly GitSyncService _gitSync;
    private readonly GitAutoSyncSnapshot _snapshot;
    private readonly DaemonTiming _timing;
    private readonly IDaemonRuntimeState _runtimeState;
    private readonly Dictionary<Guid, BackoffState> _backoff = [];
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private int _running;
    private string? _lastLoopError;

    public GitAutoSyncLoop(
        DaemonDependencies deps,
        GitSyncService gitSync,
        GitAutoSyncSnapshot snapshot,
        DaemonTiming timing,
        IDaemonRuntimeState runtimeState)
    {
        _http = deps.Http;
        _loadSettings = deps.LoadSettings;
        _gitSync = gitSync;
        _snapshot = snapshot;
        _timing = timing;
        _runtimeState = runtimeState;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return;

        try
        {
            await _timing.DelayAsync(_timing.GitAutoSyncStartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(ct);
                    SetLoopError(null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetLoopError(ex.GetType().Name);
                }

                WorkstationSettings settings;
                try { settings = _loadSettings(); }
                catch (Exception)
                {
                    SetLoopError("SettingsUnavailable");
                    settings = new WorkstationSettings();
                }

                var interval = Math.Clamp(settings.AutoSyncIntervalMinutes, 2, 120);
                var jitter = Math.Clamp(_timing.GitAutoSyncJitterFactor(), -0.2, 0.2);
                var delay = TimeSpan.FromMinutes(interval * (1d + jitter));
                try { await _timing.DelayAsync(delay, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/v1/devices/me/project-runtimes", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Project runtime discovery failed with HTTP " + (int)response.StatusCode);
        var projects = await response.Content.ReadFromJsonAsync<List<DeviceProjectRuntimeDto>>(_json, ct) ?? [];
        var ids = projects.Select(project => project.ProjectId).ToHashSet();
        _snapshot.KeepProjects(ids);

        var settings = _loadSettings();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.ManualGitSyncActive || string.IsNullOrWhiteSpace(project.LocalRoot))
                continue;

            var now = _timing.TimeProvider.GetUtcNow();
            if (project.GitLandActive)
            {
                RecordSkipped(project, "GitLandActive", now);
                continue;
            }
            if (project.CheckoutMutationActive)
            {
                RecordSkipped(project, "CheckoutMutationActive", now);
                continue;
            }
            if (_backoff.TryGetValue(project.ProjectId, out var retry) && retry.RetryAt > now)
                continue;

            var status = await ExecuteGitSyncAsync(project, GitSyncMode.Status, automatic: true, ct);
            if (status.Error == "NotGitRepository")
            {
                RecordSkipped(project, "NotGitRepository", now);
                continue;
            }
            if (status.Error == "GitSyncBusy")
            {
                RecordSkipped(project, "GitSyncBusy", now);
                continue;
            }
            if (status.Error is not null || status.Result is null)
            {
                RecordFailure(project, status.Error ?? "GitSyncResultUnavailable", now);
                continue;
            }

            var result = status.Result;
            if (result.Outcome == GitSyncOutcome.Failed)
            {
                RecordFailure(project, result.ReasonCode?.ToString() ?? "GitFetchFailed", now, result);
                continue;
            }

            _backoff.Remove(project.ProjectId);
            var previous = _snapshot.Get(project.ProjectId);
            var lastAutoPull = previous?.LastAutoPull;
            GitAutoSyncError? lastError = null;
            string? blockedReason = null;
            var current = result;

            if (string.Equals(settings.AutoPullMode, "WhenSafe", StringComparison.OrdinalIgnoreCase)
                && result.Behind > 0
                && result.Clean
                && !result.Detached
                && result.HasUpstream
                && !result.Diverged)
            {
                var permission = await RecheckProjectMutationStateAsync(project.ProjectId, ct);
                if (permission.Error is not null)
                {
                    RecordFailure(project, permission.Error, _timing.TimeProvider.GetUtcNow(), result);
                    continue;
                }
                if (permission.Project is null || permission.Project.ManualGitSyncActive)
                {
                    RecordSkipped(project, "ManualGitSyncActive", _timing.TimeProvider.GetUtcNow());
                    continue;
                }
                if (permission.Project.GitLandActive)
                {
                    RecordSkipped(project, "GitLandActive", _timing.TimeProvider.GetUtcNow());
                    continue;
                }
                if (permission.Project.CheckoutMutationActive)
                {
                    RecordSkipped(project, "CheckoutMutationActive", _timing.TimeProvider.GetUtcNow());
                    continue;
                }

                var pull = await ExecuteGitSyncAsync(project, GitSyncMode.PullFastForward, automatic: true, ct);
                if (pull.Result is { } pullResult)
                {
                    current = pullResult;
                    if (pullResult.Outcome == GitSyncOutcome.FastForwarded
                        && IsSha(pullResult.HeadBefore) && IsSha(pullResult.HeadAfter))
                    {
                        lastAutoPull = new GitAutoPullEvent(
                            _timing.TimeProvider.GetUtcNow(),
                            pullResult.HeadBefore!,
                            pullResult.HeadAfter!,
                            result.Behind);
                    }
                    else if (pullResult.Outcome == GitSyncOutcome.Refused)
                    {
                        blockedReason = pullResult.ReasonCode?.ToString() ?? "PullRefused";
                    }
                    else if (pullResult.Outcome == GitSyncOutcome.Failed)
                    {
                        var code = pullResult.ReasonCode?.ToString() ?? "GitFetchFailed";
                        RecordFailure(project, code, _timing.TimeProvider.GetUtcNow(), pullResult);
                        continue;
                    }
                }
                else
                {
                    RecordFailure(project, pull.Error ?? "GitSyncFailed", _timing.TimeProvider.GetUtcNow(), result);
                    continue;
                }
            }
            else if (!result.HasUpstream)
                blockedReason = "NoUpstream";
            else if (result.Detached)
                blockedReason = "Detached";
            else if (result.Diverged)
                blockedReason = "Diverged";
            else if (!result.Clean)
                blockedReason = "DirtyWorkingTree";

            var state = ToState(project.ProjectId, current, _timing.TimeProvider.GetUtcNow(),
                lastAutoPull, lastError, blockedReason);
            if (_snapshot.Set(state))
                _runtimeState.Log($"repository status changed for project {project.ProjectId}: {Describe(state)}");
        }
    }

    private async Task<(DeviceProjectRuntimeDto? Project, string? Error)> RecheckProjectMutationStateAsync(
        Guid projectId,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("/v1/devices/me/project-runtimes", ct);
            if (!response.IsSuccessStatusCode)
                return (null, "RuntimeDiscoveryUnavailable");
            var projects = await response.Content.ReadFromJsonAsync<List<DeviceProjectRuntimeDto>>(_json, ct) ?? [];
            return (projects.FirstOrDefault(project => project.ProjectId == projectId), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return (null, "RuntimeDiscoveryUnavailable");
        }
    }

    private async Task<(GitSyncResultDto? Result, string? Error)> ExecuteGitSyncAsync(
        DeviceProjectRuntimeDto project,
        GitSyncMode mode,
        bool automatic,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { projectId = project.ProjectId, mode = mode.ToString() }, _json);
        var execution = await _gitSync.ExecuteAsync(
            project.LocalRoot, project.ProjectId, payload, ct, automatic: automatic);
        if (!execution.Ok)
            return (null, MapLocalFailure(execution.Error));
        try
        {
            return (JsonSerializer.Deserialize<GitSyncResultDto>(execution.Result ?? "{}", _json), null);
        }
        catch (JsonException)
        {
            return (null, "GitSyncResultMalformed");
        }
    }

    private void RecordSkipped(DeviceProjectRuntimeDto project, string reason, DateTimeOffset now)
    {
        var previous = _snapshot.Get(project.ProjectId);
        var state = new GitAutoSyncProjectState(
            project.ProjectId,
            previous?.Branch,
            previous?.Upstream,
            previous?.Ahead ?? 0,
            previous?.Behind ?? 0,
            previous?.Clean ?? false,
            previous?.Detached ?? false,
            previous?.HasUpstream ?? false,
            previous?.Diverged ?? false,
            now,
            previous?.LastAutoPull,
            previous?.LastError,
            reason);
        if (_snapshot.Set(state))
            _runtimeState.Log($"repository auto-sync skipped for project {project.ProjectId}: {reason}");
    }

    private void RecordFailure(
        DeviceProjectRuntimeDto project, string code, DateTimeOffset now, GitSyncResultDto? result = null)
    {
        var failures = _backoff.TryGetValue(project.ProjectId, out var prior)
            ? Math.Min(prior.Failures + 1, 10) : 1;
        var minutes = Math.Min(60, Math.Pow(2, failures - 1));
        _backoff[project.ProjectId] = new BackoffState(failures, now.AddMinutes(minutes));
        var previous = _snapshot.Get(project.ProjectId);
        var state = result is null
            ? new GitAutoSyncProjectState(project.ProjectId, previous?.Branch, previous?.Upstream,
                previous?.Ahead ?? 0, previous?.Behind ?? 0, previous?.Clean ?? false,
                previous?.Detached ?? false, previous?.HasUpstream ?? false, previous?.Diverged ?? false,
                now, previous?.LastAutoPull, new GitAutoSyncError(code, now), previous?.AutoPullBlockedReason)
            : ToState(project.ProjectId, result, now, previous?.LastAutoPull,
                new GitAutoSyncError(code, now), code);
        if (_snapshot.Set(state))
            _runtimeState.Log($"repository auto-sync state changed for project {project.ProjectId}: {code}");
    }

    private void SetLoopError(string? code)
    {
        if (string.Equals(_lastLoopError, code, StringComparison.Ordinal))
            return;
        _lastLoopError = code;
        if (code is not null)
            _runtimeState.Log("repository auto-sync discovery state: " + code);
    }

    private static GitAutoSyncProjectState ToState(
        Guid projectId,
        GitSyncResultDto result,
        DateTimeOffset checkedAt,
        GitAutoPullEvent? lastAutoPull,
        GitAutoSyncError? lastError,
        string? blockedReason) =>
        new(projectId, result.Branch, result.Upstream, result.Ahead, result.Behind,
            result.Clean, result.Detached, result.HasUpstream, result.Diverged,
            checkedAt, lastAutoPull, lastError, blockedReason);

    private static string Describe(GitAutoSyncProjectState state) =>
        state.AutoPullBlockedReason is not null
            ? state.AutoPullBlockedReason
            : state.Behind > 0 ? $"behind {state.Behind}" : "up to date";

    private static string MapLocalFailure(string? error)
    {
        if (error?.Contains("not a Git repository", StringComparison.OrdinalIgnoreCase) == true)
            return "NotGitRepository";
        if (error?.Equals("GitSyncBusy", StringComparison.Ordinal) == true)
            return "GitSyncBusy";
        if (error?.Contains("directory does not exist", StringComparison.OrdinalIgnoreCase) == true)
            return "ProjectDirectoryUnavailable";
        return "GitSyncFailed";
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
