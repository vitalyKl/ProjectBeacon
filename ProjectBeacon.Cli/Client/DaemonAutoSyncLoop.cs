namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using System.Text.Json;
using Application.Devices;
using ProjectBeacon.Cli.Client.AgentHarness;

internal sealed class DaemonAutoSyncLoop : IDaemonLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly GitSyncService _gitSync;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;
    private readonly Func<string, Guid, DeviceRemoteLandingTargetDto, CancellationToken,
        Task<AgentTaskGitHubRemoteLandingOperations.VerificationResult>> _verifyRemoteLanding;
    private readonly Dictionary<Guid, string> _lastStates = [];

    public DaemonAutoSyncLoop(
        DaemonDependencies deps,
        GitSyncService gitSync,
        IDaemonRuntimeState state,
        DaemonTiming timing,
        Func<string, Guid, DeviceRemoteLandingTargetDto, CancellationToken,
            Task<AgentTaskGitHubRemoteLandingOperations.VerificationResult>>? verifyRemoteLanding = null)
    {
        _http = deps.Http;
        _gitSync = gitSync;
        _state = state;
        _timing = timing;
        _verifyRemoteLanding = verifyRemoteLanding ?? AgentTaskGitHubRemoteLandingOperations.VerifyAsync;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SynchronizeEnabledProjectsAsync(ct);
                await _timing.DelayAsync(_timing.AutoSyncInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _state.Log($"AutoSync cycle failed: {exception.Message}");
                try
                {
                    await _timing.DelayAsync(_timing.ErrorBackoffInterval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task SynchronizeEnabledProjectsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("v1/devices/me/auto-sync-targets", ct);
        if (!response.IsSuccessStatusCode)
        {
            _state.Log($"AutoSync target discovery returned HTTP {(int)response.StatusCode}");
            return;
        }

        var targets = await response.Content.ReadFromJsonAsync<List<DeviceAutoSyncTargetDto>>(JsonOptions, ct)
            ?? [];
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(target.LocalRoot))
                continue;

            if (target.AutoSyncEnabled)
                await SynchronizeProjectAsync(target, ct);
            await SynchronizeRemoteLandingsAsync(target, ct);
        }
    }

    private async Task SynchronizeProjectAsync(DeviceAutoSyncTargetDto target, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            projectId = target.ProjectId,
            mode = "PullFastForward"
        }, JsonOptions);
        var branch = await _gitSync.ExecuteAsync(
            target.LocalRoot, target.ProjectId, payload, ct);
        if (!branch.Ok)
        {
            RecordState(target.ProjectId, "branch-error:" + branch.Error,
                $"AutoSync project {target.ProjectId}: fetch/fast-forward failed: {branch.Error}");
        }
        else
        {
            var (outcome, reason, behind) = ReadBranchOutcome(branch.Result);
            var branchState = $"branch:{outcome}:{reason}:{behind}";
            if (string.Equals(outcome, "FastForwarded", StringComparison.Ordinal))
            {
                RecordState(target.ProjectId, branchState,
                    $"AutoSync project {target.ProjectId}: upstream fast-forward completed.");
            }
            else if (!string.Equals(outcome, "UpToDate", StringComparison.Ordinal))
            {
                RecordState(target.ProjectId, branchState,
                    $"AutoSync project {target.ProjectId}: repository was not advanced ({outcome}, {reason ?? "no reason"}).");
            }
            else
            {
                _lastStates[target.ProjectId] = branchState;
            }
        }

        // Tags are a ref-only operation. It may run even when the working tree is dirty,
        // but GitSyncService refuses to change refs while an agent session is active.
        var tags = await _gitSync.SyncTagsAsync(target.LocalRoot, ct);
        if (!tags.Success)
        {
            RecordState(target.ProjectId, "tag-error:" + tags.Error,
                $"AutoSync project {target.ProjectId}: tag fetch failed: {tags.Error}");
        }
        else if (!tags.Skipped && tags.NewTags > 0)
        {
            RecordState(target.ProjectId, $"tags:{tags.NewTags}",
                $"AutoSync project {target.ProjectId}: fetched {tags.NewTags} new tag(s).");
        }


    }

    private async Task SynchronizeRemoteLandingsAsync(DeviceAutoSyncTargetDto target, CancellationToken ct)
    {
        foreach (var remoteLanding in target.RemoteLandings ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var verification = await _verifyRemoteLanding(
                target.LocalRoot, target.ProjectId, remoteLanding, ct);
            if (verification.StatusRequest is { } statusRequest)
            {
                using var statusResponse = await _http.PostAsJsonAsync(
                    "v1/devices/me/remote-landings/status",
                    statusRequest, JsonOptions, ct);
                if (statusResponse.IsSuccessStatusCode)
                {
                    var result = await statusResponse.Content.ReadFromJsonAsync<DeviceRemoteLandingStatusResult>(
                        JsonOptions, ct);
                    var persisted = result?.AlreadyRecorded == true ? "already persisted" : "persisted";
                    RecordState(remoteLanding.TaskId, "remote-landing-enqueued:" + result?.MergeRequestStatus,
                        $"AutoSync task {remoteLanding.TaskId}: GitHub async merge request was confirmed enqueued and its state was {persisted}. The task remains open until the merge is verified.");
                }
                else
                {
                    var errorBody = await statusResponse.Content.ReadAsStringAsync(ct);
                    var state = $"remote-landing-status-http:{(int)statusResponse.StatusCode}";
                    RecordState(remoteLanding.TaskId, state,
                        $"AutoSync task {remoteLanding.TaskId}: GitHub enqueue status was not persisted (HTTP {(int)statusResponse.StatusCode}). {Clip(errorBody)}");
                }
                continue;
            }

            if (verification.FailureRequest is { } failureRequest)
            {
                using var failureResponse = await _http.PostAsJsonAsync(
                    "v1/devices/me/remote-landings/failure",
                    failureRequest, JsonOptions, ct);
                if (failureResponse.IsSuccessStatusCode)
                {
                    var result = await failureResponse.Content.ReadFromJsonAsync<DeviceRemoteLandingFailureResult>(
                        JsonOptions, ct);
                    RecordState(remoteLanding.TaskId, "remote-landing-failed:" + result?.FailureCode,
                        $"AutoSync task {remoteLanding.TaskId}: GitHub merge request did not complete ({result?.FailureCode ?? "unknown"}). Landing attempt is {result?.AttemptStatus ?? "Failed"}; request a fresh Preview after reviewing the failure.");
                }
                else
                {
                    var errorBody = await failureResponse.Content.ReadAsStringAsync(ct);
                    var state = $"remote-landing-failure-http:{(int)failureResponse.StatusCode}";
                    RecordState(remoteLanding.TaskId, state,
                        $"AutoSync task {remoteLanding.TaskId}: GitHub reported a definitive merge failure, but Beacon did not accept the failure proof (HTTP {(int)failureResponse.StatusCode}). {Clip(errorBody)}");
                }
                continue;
            }

            if (verification.Request is null)
            {
                if (!string.IsNullOrWhiteSpace(verification.Status))
                    RecordState(remoteLanding.TaskId, "remote-landing-check:" + verification.Status,
                        $"AutoSync task {remoteLanding.TaskId}: {verification.Status}");
                continue;
            }

            using var response = await _http.PostAsJsonAsync(
                "v1/devices/me/remote-landings/reconcile",
                verification.Request, JsonOptions, ct);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<DeviceRemoteLandingReconciliationResult>(
                    JsonOptions, ct);
                var status = result?.AlreadyRecorded == true ? "already-reconciled" : "reconciled";
                RecordState(remoteLanding.TaskId, status,
                    $"AutoSync task {remoteLanding.TaskId}: GitHub PR #{verification.Request.PullRequestNumber} merge was reconciled and the task is {result?.TaskStatus ?? "closed"}.");
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var state = $"remote-landing-http:{(int)response.StatusCode}";
                RecordState(remoteLanding.TaskId, state,
                    $"AutoSync task {remoteLanding.TaskId}: remote merge proof was not accepted (HTTP {(int)response.StatusCode}). {Clip(errorBody)}");
            }
        }
    }

    private static string Clip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 500 ? safe : safe[..500];
    }

    private void RecordState(Guid projectId, string state, string message)
    {
        if (_lastStates.TryGetValue(projectId, out var previous)
            && string.Equals(previous, state, StringComparison.Ordinal))
            return;
        _lastStates[projectId] = state;
        _state.Log(message);
    }

    private static (string Outcome, string? Reason, int Behind) ReadBranchOutcome(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ("Unknown", null, 0);
        try
        {
            // GitSyncService currently serializes enum values numerically and record
            // properties in PascalCase. Web defaults keep property matching compatible.
            var result = JsonSerializer.Deserialize<GitSyncResultDto>(json, JsonOptions);
            return result is null
                ? ("Unknown", null, 0)
                : (result.Outcome.ToString(), result.ReasonCode?.ToString(), result.Behind);
        }
        catch (JsonException)
        {
            return ("Unknown", null, 0);
        }
    }
}
