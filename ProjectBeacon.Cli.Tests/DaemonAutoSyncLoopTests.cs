namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Devices;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class DaemonAutoSyncLoopTests
{
    [Fact]
    public async Task ReconcilesVerifiedBeaconLandingWhenAutoSyncIsDisabledWithoutRunningGitSync()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var reviewRunId = Guid.NewGuid();
        const string sourceBranch = "beacon/task/0123456789abcdef0123456789abcdef";
        const string targetBranch = "main";
        const string repository = "acme/project-beacon";
        const string pullRequestUrl = "https://github.com/acme/project-beacon/pull/42";
        var sourceSha = new string('a', 40);
        var mergeSha = new string('b', 40);
        var remoteBaseSha = new string('c', 40);
        var now = DateTime.UtcNow;

        var remoteLanding = new DeviceRemoteLandingTargetDto(
            taskId, reviewRunId, sourceBranch, targetBranch, sourceSha);
        var target = new DeviceAutoSyncTargetDto(
            projectId,
            "/workspace/remote-landing-only",
            PolicyRevision: 3,
            AutoSyncEnabled: false,
            RemoteLandings: [remoteLanding]);
        var request = new DeviceRemoteLandingReconciliationRequest(
            projectId,
            taskId,
            reviewRunId,
            repository,
            42,
            pullRequestUrl,
            sourceBranch,
            targetBranch,
            sourceSha,
            mergeSha,
            remoteBaseSha,
            now.AddMinutes(-2),
            now);
        var response = new DeviceRemoteLandingReconciliationResult(
            Guid.NewGuid(),
            projectId,
            taskId,
            42,
            pullRequestUrl,
            sourceSha,
            mergeSha,
            targetBranch,
            remoteBaseSha,
            "Done",
            "Closed",
            AlreadyRecorded: false);

        var api = new AutoSyncLandingApi(target, response);
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var state = new DaemonRuntimeState("http://localhost", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var timing = new DaemonTiming
        {
            DelayAsync = (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var deps = new DaemonDependencies(
            http,
            null!,
            () => new WorkstationSettings(),
            _ => new UnavailableAgentRuntime("Unused AutoSync-off loop test runtime"));
        var verifyCalls = 0;
        var loop = new DaemonAutoSyncLoop(
            deps,
            gitSync: null!,
            state,
            timing,
            (root, verifiedProjectId, landing, _) =>
            {
                Interlocked.Increment(ref verifyCalls);
                Assert.Equal("/workspace/remote-landing-only", root);
                Assert.Equal(projectId, verifiedProjectId);
                Assert.Equal(taskId, landing.TaskId);
                return Task.FromResult(
                    new AgentTaskGitHubRemoteLandingOperations.VerificationResult(
                        request, "GitHub merge verified by the pinned workstation."));
            });

        await loop.RunAsync(cancellation.Token);

        Assert.Equal(1, api.TargetRequests);
        Assert.Equal(1, api.ReconciliationRequests);
        Assert.Equal(1, verifyCalls);
        Assert.NotNull(api.ReconciliationBody);
        using var posted = JsonDocument.Parse(api.ReconciliationBody);
        Assert.Equal(projectId, posted.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(taskId, posted.RootElement.GetProperty("taskId").GetGuid());
        Assert.Equal(mergeSha, posted.RootElement.GetProperty("mergeCommitSha").GetString());
        Assert.DoesNotContain(
            state.Snapshot.Log,
            line => line.Contains("AutoSync cycle failed", StringComparison.Ordinal));
        Assert.Contains(
            state.Snapshot.Log,
            line => line.Contains("merge was reconciled", StringComparison.Ordinal));
    }

    private sealed class AutoSyncLandingApi(
        DeviceAutoSyncTargetDto target,
        DeviceRemoteLandingReconciliationResult reconciliation) : HttpMessageHandler
    {
        private int _targetRequests;
        private int _reconciliationRequests;

        public int TargetRequests => Volatile.Read(ref _targetRequests);
        public int ReconciliationRequests => Volatile.Read(ref _reconciliationRequests);
        public string? ReconciliationBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && path == "/v1/devices/me/auto-sync-targets")
            {
                Interlocked.Increment(ref _targetRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { target })
                };
            }

            if (request.Method == HttpMethod.Post
                && path == "/v1/devices/me/remote-landings/reconcile")
            {
                Interlocked.Increment(ref _reconciliationRequests);
                ReconciliationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(reconciliation)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
