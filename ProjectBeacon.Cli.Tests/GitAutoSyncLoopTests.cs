namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Cli.Client;

public sealed class GitAutoSyncLoopTests
{
    [Fact]
    public async Task RunCycle_FetchesAndFastForwardsCleanCheckoutWithoutQueuingCommands()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-loop-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(parent);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");

            await Git(parent, "clone", remote, seed);
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "remote.txt"), "from remote");
            await Git(seed, "add", "remote.txt");
            await Git(seed, "commit", "-m", "advance remote");
            await Git(seed, "push", "origin", "main");

            var projectId = Guid.NewGuid();
            var requests = new List<string>();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var http = new HttpClient(new RecordingHandler((request, _) =>
            {
                requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming();
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var state = new DaemonRuntimeState("https://beacon.test", null);
            var loop = new GitAutoSyncLoop(dependencies, new GitSyncService(), snapshot, timing, state);

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(new[]
            {
                "GET /v1/devices/me/project-runtimes",
                "GET /v1/devices/me/project-runtimes"
            }, requests);
            Assert.Equal("from remote", await File.ReadAllTextAsync(Path.Combine(root, "remote.txt")));
            var currentHead = await Git(root, "rev-parse", "HEAD");
            var remoteHead = await Git(root, "rev-parse", "refs/remotes/origin/main");
            Assert.Equal(remoteHead, currentHead);

            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal("main", status.Branch);
            Assert.Equal("origin/main", status.Upstream);
            Assert.Equal(0, status.Behind);
            Assert.True(status.Clean);
            Assert.NotNull(status.LastAutoPull);
            Assert.Equal(1, status.LastAutoPull.Commits);
            Assert.Equal(remoteHead, status.LastAutoPull.ToSha);
            Assert.Equal(40, status.LastAutoPull.FromSha.Length);
            Assert.Null(status.LastError);
            var pullLog = Assert.Single(
                state.Snapshot.Log,
                line => line.Contains("repository auto-pull completed", StringComparison.Ordinal));
            Assert.Contains("1 commits", pullLog, StringComparison.Ordinal);
            Assert.Contains(status.LastAutoPull.FromSha + " -> " + status.LastAutoPull.ToSha, pullLog, StringComparison.Ordinal);
            Assert.Contains(status.LastAutoPull.At.ToString("O"), pullLog, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_OffModeFetchesStatusButDoesNotFastForwardOrQueueCommands()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-off-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(parent);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");

            await Git(parent, "clone", remote, seed);
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "remote.txt"), "from remote");
            await Git(seed, "add", "remote.txt");
            await Git(seed, "commit", "-m", "advance remote");
            await Git(seed, "push", "origin", "main");

            var projectId = Guid.NewGuid();
            var requests = new List<string>();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var http = new HttpClient(new RecordingHandler((request, _) =>
            {
                requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "Off", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming();
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var state = new DaemonRuntimeState("https://beacon.test", null);
            var loop = new GitAutoSyncLoop(dependencies, new GitSyncService(), snapshot, timing, state);
            var headBefore = await Git(root, "rev-parse", "HEAD");

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(new[] { "GET /v1/devices/me/project-runtimes" }, requests);
            Assert.Equal(headBefore, await Git(root, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(root, "remote.txt")));
            var remoteHead = await Git(root, "rev-parse", "refs/remotes/origin/main");
            Assert.NotEqual(headBefore, remoteHead);

            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal(1, status.Behind);
            Assert.True(status.Clean);
            Assert.Null(status.LastAutoPull);
            Assert.Null(status.LastError);
            Assert.NotNull(status.CheckedAt);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_RecordsSanitizedFetchFailureAndSchedulesRetryBeforeInterval()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-backoff-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var remote = Path.Combine(parent, "origin.git");
        Directory.CreateDirectory(parent);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");
            await Git(root, "remote", "set-url", "origin", Path.Combine(parent, "missing-remote.git"));

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var requests = 0;
            using var http = new HttpClient(new RecordingHandler((request, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "Off", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-24T12:00:00Z"));
            var timing = new DaemonTiming(timeProvider) { GitAutoSyncStartupDelay = TimeSpan.Zero };
            var delays = new List<TimeSpan>();
            using var cancellation = new CancellationTokenSource();
            timing.DelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                timeProvider.Advance(delay);
                var scheduledRetryElapsed = delays.Skip(1)
                    .Aggregate(TimeSpan.Zero, (total, item) => total + item);
                if (delays.Count > 1 && scheduledRetryElapsed >= TimeSpan.FromMinutes(1))
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(1, requests);
            Assert.Equal(TimeSpan.Zero, delays[0]);
            Assert.Equal(TimeSpan.FromMinutes(1),
                delays.Skip(1).Aggregate(TimeSpan.Zero, (total, item) => total + item));
            Assert.All(delays.Skip(1), delay => Assert.Equal(TimeSpan.FromSeconds(15), delay));
            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal("FetchFailed", status.LastError?.Code);
            Assert.NotNull(status.LastError?.At);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(snapshot.ToProbeValue()), StringComparison.Ordinal);
            Assert.DoesNotContain("fatal:", JsonSerializer.Serialize(snapshot.ToProbeValue()), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_StillReportsStatusWhenLocalSettingsAreUnavailableAndNeverAutoPulls()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-settings-unavailable-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        Directory.CreateDirectory(root);

        try
        {
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var requests = 0;
            using var http = new HttpClient(new RecordingHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };
            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => throw new InvalidDataException("settings file is invalid"),
                _ => null!);
            var timing = new DaemonTiming { GitAutoSyncStartupDelay = TimeSpan.Zero };
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));
            var headBefore = await Git(root, "rev-parse", "HEAD");

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(1, requests);
            Assert.Equal(headBefore, await Git(root, "rev-parse", "HEAD"));
            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal("main", status.Branch);
            Assert.False(status.HasUpstream);
            Assert.Equal("NoUpstream", status.AutoPullBlockedReason);
            Assert.Null(status.LastAutoPull);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://[redacted]@github.com/org/repo.git'", "AuthenticationFailed")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "AuthenticationFailed")]
    [InlineData("fatal: unable to access remote: Could not resolve host", "FetchFailed")]
    public void ClassifyFailure_UsesSanitizedDiagnosticWithoutPublishingIt(string diagnostic, string expected)
    {
        var result = new GitSyncResultDto(
            "main", "origin/main", 0, 1, true, false, true, false,
            new string('a', 40), new string('a', 40),
            GitSyncOutcome.Failed, GitSyncRefusalCode.FetchFailed, diagnostic);

        Assert.Equal(expected, GitAutoSyncLoop.ClassifyFailure(result));
    }

    [Theory]
    [InlineData("/workspace/project", "/workspace/project/", true)]
    [InlineData("/workspace/project-a", "/workspace/project-b", false)]
    [InlineData("", "/workspace/project", false)]
    public void SameLocalRoot_RequiresTheRuntimePathToRemainTheSame(string oldRoot, string newRoot, bool expected)
    {
        Assert.Equal(expected, GitAutoSyncLoop.SameLocalRoot(oldRoot, newRoot));
    }

    [Fact]
    public void CalculateNextDelay_WakesForRetryBeforeTheConfiguredInterval()
    {
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

        Assert.Equal(TimeSpan.FromMinutes(1), GitAutoSyncLoop.CalculateNextDelay(
            10, 0, [now.AddMinutes(1)], now));
        Assert.Equal(TimeSpan.FromMinutes(2), GitAutoSyncLoop.CalculateNextDelay(
            10, 0, [now.AddMinutes(4), now.AddMinutes(2)], now));
        Assert.Equal(TimeSpan.FromMinutes(10), GitAutoSyncLoop.CalculateNextDelay(
            10, 0, [], now));
        Assert.Equal(TimeSpan.FromMinutes(60), GitAutoSyncLoop.CalculateNextDelay(
            120, 0.2, [now.AddHours(1)], now));
        Assert.Equal(TimeSpan.FromMinutes(12), GitAutoSyncLoop.CalculateNextDelay(
            10, 0.2, [], now));
    }

    [Fact]
    public async Task DelayUntilNextCycle_ReturnsImmediatelyWhenTheWorkstationResumesFromSleep()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-24T12:00:00Z"));
        var timing = new DaemonTiming(timeProvider)
        {
            GitAutoSyncWakeCheckInterval = TimeSpan.FromSeconds(15),
            GitAutoSyncWakeGracePeriod = TimeSpan.FromSeconds(5)
        };
        var delays = new List<TimeSpan>();
        timing.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            timeProvider.Advance(delay + TimeSpan.FromMinutes(30));
            return Task.CompletedTask;
        };
        using var http = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var dependencies = new DaemonDependencies(http, null!, () => new WorkstationSettings(), _ => null!);
        var loop = new GitAutoSyncLoop(
            dependencies,
            new GitSyncService(),
            new GitAutoSyncSnapshot(),
            timing,
            new DaemonRuntimeState("https://beacon.test", null));

        await loop.DelayUntilNextCycleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(delays));
        Assert.True(GitAutoSyncLoop.IsResumeGap(
            TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5)));
        Assert.False(GitAutoSyncLoop.IsResumeGap(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunCycle_DoesNotPullOldDirectoryWhenRuntimeRootChangesAfterStatusFetch()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-runtime-change-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var movedRoot = Path.Combine(parent, "moved-project");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(movedRoot);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");

            await Git(parent, "clone", remote, seed);
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "remote.txt"), "new remote");
            await Git(seed, "add", "remote.txt");
            await Git(seed, "commit", "-m", "advance remote");
            await Git(seed, "push", "origin", "main");

            var projectId = Guid.NewGuid();
            var firstRuntimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var changedRuntimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, movedRoot, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var requests = 0;
            using var http = new HttpClient(new RecordingHandler((_, _) =>
            {
                var json = Interlocked.Increment(ref requests) == 1 ? firstRuntimeJson : changedRuntimeJson;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming();
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));
            var headBefore = await Git(root, "rev-parse", "HEAD");

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(2, requests);
            Assert.Equal(headBefore, await Git(root, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(root, "remote.txt")));
            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal(1, status.Behind);
            Assert.Equal("RuntimeChanged", status.AutoPullBlockedReason);
            Assert.Null(status.LastAutoPull);
            Assert.Null(status.LastError);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_SkipsLocalBranchUpstreamInsteadOfFastForwardingIt()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-local-upstream-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        Directory.CreateDirectory(root);

        try
        {
            await Git(root, "init", "-b", "main");
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");

            await Git(root, "branch", "local-upstream");
            await Git(root, "checkout", "local-upstream");
            await File.WriteAllTextAsync(Path.Combine(root, "local-upstream.txt"), "local-only");
            await Git(root, "add", "local-upstream.txt");
            await Git(root, "commit", "-m", "advance local upstream");
            var localHead = await Git(root, "rev-parse", "HEAD");

            await Git(root, "checkout", "main");
            await Git(root, "config", "branch.main.remote", ".");
            await Git(root, "config", "branch.main.merge", "refs/heads/local-upstream");
            var mainHead = await Git(root, "rev-parse", "HEAD");

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var http = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                })))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };
            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming();
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(mainHead, await Git(root, "rev-parse", "HEAD"));
            Assert.Equal(localHead, await Git(root, "rev-parse", "refs/heads/local-upstream"));
            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal(1, status.Behind);
            Assert.Equal("NoRemote", status.AutoPullBlockedReason);
            Assert.Null(status.LastAutoPull);
            Assert.Null(status.LastError);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_ReportsDetachedHeadInsteadOfMissingUpstream()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-detached-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        Directory.CreateDirectory(root);

        try
        {
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "checkout", "--detach", "HEAD");

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var http = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                })))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "Off", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming();
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));

            await loop.RunAsync(cancellation.Token);

            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.True(status.Detached);
            Assert.False(status.HasUpstream);
            Assert.Equal("Detached", status.AutoPullBlockedReason);
            Assert.Null(status.LastError);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true, false, false, "ManualGitSyncActive")]
    [InlineData(false, true, false, "GitLandActive")]
    [InlineData(false, false, true, "CheckoutMutationActive")]
    public async Task RunCycle_SkipsProjectsWithActiveCheckoutMutations(
        bool manualGitSyncActive,
        bool gitLandActive,
        bool checkoutMutationActive,
        string expectedReason)
    {
        var projectId = Guid.NewGuid();
        var runtimeJson = JsonSerializer.Serialize(
            new[]
            {
                new DeviceProjectRuntimeDto(
                    projectId,
                    Path.Combine(Path.GetTempPath(), "not-used-" + Guid.NewGuid().ToString("N")),
                    manualGitSyncActive,
                    gitLandActive,
                    checkoutMutationActive)
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
            });
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var dependencies = new DaemonDependencies(
            http,
            null!,
            () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
            _ => null!);
        var timing = new DaemonTiming { GitAutoSyncStartupDelay = TimeSpan.Zero };
        using var cancellation = new CancellationTokenSource();
        var delays = 0;
        timing.DelayAsync = (_, _) =>
        {
            if (Interlocked.Increment(ref delays) == 2)
                cancellation.Cancel();
            return Task.CompletedTask;
        };
        var snapshot = new GitAutoSyncSnapshot();
        var loop = new GitAutoSyncLoop(
            dependencies, new GitSyncService(), snapshot, timing,
            new DaemonRuntimeState("https://beacon.test", null));

        await loop.RunAsync(cancellation.Token);

        Assert.Equal(1, requests);
        var status = snapshot.Get(projectId);
        Assert.NotNull(status);
        Assert.Equal(expectedReason, status.AutoPullBlockedReason);
        Assert.Null(status.CheckedAt);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task RunCycle_DoesNotPullWhileSnapshotSessionIsActive()
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-snapshot-active-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(parent);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");

            await Git(parent, "clone", remote, seed);
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "remote.txt"), "from remote");
            await Git(seed, "add", "remote.txt");
            await Git(seed, "commit", "-m", "advance remote");
            await Git(seed, "push", "origin", "main");

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var requests = 0;
            using var http = new HttpClient(new RecordingHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };
            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming { GitAutoSyncStartupDelay = TimeSpan.Zero };
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies, new GitSyncService(), snapshot, timing,
                new DaemonRuntimeState("https://beacon.test", null));
            var headBefore = await Git(root, "rev-parse", "HEAD");

            using var activeSnapshot = GitRepositoryOperationGate.RegisterSession(root);
            await loop.RunAsync(cancellation.Token);

            Assert.Equal(2, requests);
            Assert.Equal(headBefore, await Git(root, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(root, "remote.txt")));
            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal(1, status.Behind);
            Assert.Equal("ActiveSessions", status.AutoPullBlockedReason);
            Assert.Null(status.LastAutoPull);
            Assert.Null(status.LastError);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("dirty", "DirtyWorkingTree")]
    [InlineData("diverged", "Diverged")]
    [InlineData("no-upstream", "NoUpstream")]
    public async Task RunCycle_DoesNotPullUnsafeRepositoryStates(string scenario, string expectedReason)
    {
        var parent = Path.Combine(Path.GetTempPath(), "beacon-auto-sync-unsafe-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "project");
        var remote = Path.Combine(parent, "origin.git");
        var seed = Path.Combine(parent, "seed");
        Directory.CreateDirectory(parent);

        try
        {
            await Git(parent, "init", "--bare", "--initial-branch=main", remote);
            await Git(parent, "init", "-b", "main", root);
            await ConfigureIdentity(root);
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");
            await Git(root, "remote", "add", "origin", remote);
            await Git(root, "push", "-u", "origin", "main");

            await Git(parent, "clone", remote, seed);
            await ConfigureIdentity(seed);
            await File.WriteAllTextAsync(Path.Combine(seed, "remote.txt"), "remote commit");
            await Git(seed, "add", "remote.txt");
            await Git(seed, "commit", "-m", "advance origin");
            await Git(seed, "push", "origin", "main");

            if (scenario == "dirty")
            {
                await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "local uncommitted change");
            }
            else if (scenario == "diverged")
            {
                await File.WriteAllTextAsync(Path.Combine(root, "local.txt"), "local branch commit");
                await Git(root, "add", "local.txt");
                await Git(root, "commit", "-m", "advance local branch");
            }
            else if (scenario == "no-upstream")
            {
                await Git(root, "branch", "--unset-upstream");
            }

            var projectId = Guid.NewGuid();
            var runtimeJson = JsonSerializer.Serialize(
                new[] { new DeviceProjectRuntimeDto(projectId, root, false, false) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var requests = new List<string>();
            using var http = new HttpClient(new RecordingHandler((request, _) =>
            {
                requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeJson, Encoding.UTF8, "application/json")
                });
            }))
            {
                BaseAddress = new Uri("https://beacon.test/")
            };

            var dependencies = new DaemonDependencies(
                http,
                null!,
                () => new WorkstationSettings { AutoPullMode = "WhenSafe", AutoSyncIntervalMinutes = 10 },
                _ => null!);
            var timing = new DaemonTiming { GitAutoSyncStartupDelay = TimeSpan.Zero };
            using var cancellation = new CancellationTokenSource();
            var delays = 0;
            timing.DelayAsync = (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2)
                    cancellation.Cancel();
                return Task.CompletedTask;
            };
            var snapshot = new GitAutoSyncSnapshot();
            var loop = new GitAutoSyncLoop(
                dependencies,
                new GitSyncService(),
                snapshot,
                timing,
                new DaemonRuntimeState("https://beacon.test", null));
            var headBefore = await Git(root, "rev-parse", "HEAD");
            var dirtyFileBefore = scenario == "dirty"
                ? await File.ReadAllTextAsync(Path.Combine(root, "README.md"))
                : null;

            await loop.RunAsync(cancellation.Token);

            Assert.Equal(new[] { "GET /v1/devices/me/project-runtimes" }, requests);
            Assert.Equal(headBefore, await Git(root, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(root, "remote.txt")));
            if (dirtyFileBefore is not null)
                Assert.Equal(dirtyFileBefore, await File.ReadAllTextAsync(Path.Combine(root, "README.md")));

            var status = snapshot.Get(projectId);
            Assert.NotNull(status);
            Assert.Equal(expectedReason, status.AutoPullBlockedReason);
            Assert.Null(status.LastAutoPull);
            Assert.Null(status.LastError);
            if (scenario is "dirty" or "diverged")
                Assert.Equal(1, status.Behind);
            if (scenario == "dirty")
                Assert.False(status.Clean);
            if (scenario == "diverged")
            {
                Assert.True(status.Diverged);
                Assert.Equal(1, status.Ahead);
            }
            if (scenario == "no-upstream")
                Assert.False(status.HasUpstream);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static async Task ConfigureIdentity(string directory)
    {
        await Git(directory, "config", "user.name", "ProjectBeacon Tests");
        await Git(directory, "config", "user.email", "tests@projectbeacon.local");
        await Git(directory, "config", "commit.gpgsign", "false");
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Git in the AutoSync loop test.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} exceeded 30 seconds.");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
