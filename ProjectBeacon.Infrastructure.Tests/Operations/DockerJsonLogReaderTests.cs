namespace ProjectBeacon.Infrastructure.Tests.Operations;

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.DockerLogProxy;
using ProjectBeacon.OpsAgent;

public sealed class DockerJsonLogReaderTests
{
    [Fact]
    public async Task DockerEngineReader_ReturnsOnlyRequestedComposeProjectAndService()
    {
        var webId = new string('a', 64);
        var foreignId = new string('b', 64);
        var workerId = new string('c', 64);
        var containers = new[]
        {
            Container(webId, "beacon-alpha", "web", "/beacon-alpha-web-1", "running"),
            Container(foreignId, "other-project", "web", "/other-web-1", "running"),
            Container(workerId, "beacon-alpha", "worker", "/beacon-alpha-worker-1", "running")
        };
        var handler = new FakeDockerEngineHandler(containers, [
            ("web-one", "2026-11-11T08:00:00.0000000Z", (byte)1),
            ("web-two", "2026-11-11T08:00:01.0000000Z", (byte)2),
            ("web-three", "2026-11-11T08:00:02.0000000Z", (byte)1)
        ]);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://docker") };
        var reader = CreateReader(client);

        var response = await reader.ReadAsync(
            PlatformLogTarget.Web, 2, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Equal(PlatformLogTarget.Web, response.Service);
        Assert.Equal("/beacon-alpha-web-1", response.ContainerName);
        Assert.Equal(new[] { "web-two", "web-three" }, response.Entries.Select(entry => entry.Message));
        Assert.Equal(new[] { "stderr", "stdout" }, response.Entries.Select(entry => entry.Stream));
        Assert.Single(handler.Requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith("/logs", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains(foreignId, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains(workerId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DockerEngineReader_ReturnsNullWhenNoContainerHasTheAllowlistedProjectLabel()
    {
        var id = new string('d', 64);
        var handler = new FakeDockerEngineHandler(
            [Container(id, "not-beacon-alpha", "web", "/foreign-web", "running")],
            []);
        var reader = CreateReader(new HttpClient(handler) { BaseAddress = new Uri("http://docker") });

        var response = await reader.ReadAsync(
            PlatformLogTarget.Web, 100, TestContext.Current.CancellationToken);

        Assert.Null(response);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/logs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DockerEngineReader_ValidatesTailBeforeCallingEngine()
    {
        var handler = new FakeDockerEngineHandler([], []);
        var reader = CreateReader(new HttpClient(handler) { BaseAddress = new Uri("http://docker") });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadAsync(PlatformLogTarget.Web, 501, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void PlatformLogRequestSignature_BindsMethodPathTimestampAndNonce()
    {
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        const string timestamp = "1794384000";
        const string nonce = "0123456789abcdef0123456789abcdef";
        const string method = "GET";
        const string path = "/internal/v1/logs/web?tail=100";
        var signature = PlatformLogRequestSignature.Create(secret, timestamp, nonce, method, path);

        Assert.True(PlatformLogRequestSignature.Verify(secret, timestamp, nonce, method, path, signature));
        Assert.False(PlatformLogRequestSignature.Verify(secret, timestamp, nonce, method, "/internal/v1/logs/worker?tail=100", signature));
        Assert.False(PlatformLogRequestSignature.Verify(secret, timestamp, nonce, "POST", path, signature));
        Assert.False(PlatformLogRequestSignature.Verify(secret, "1794384001", nonce, method, path, signature));
    }

    [Fact]
    public void ReplayNonceStore_RejectsDuplicateNonces()
    {
        var store = new ReplayNonceStore();
        var now = DateTimeOffset.Parse("2026-11-11T08:00:00Z");
        const string nonce = "0123456789abcdef0123456789abcdef";

        Assert.True(store.TryAccept(nonce, now));
        Assert.False(store.TryAccept(nonce, now.AddSeconds(1)));
    }

    private static DockerEngineLogReader CreateReader(HttpClient client)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DOCKER_SOCKET_PATH"] = "/var/run/docker.sock",
                ["DOCKER_LOG_PROXY_COMPOSE_PROJECT"] = "beacon-alpha",
                ["DOCKER_LOG_PROXY_SHARED_SECRET"] = Convert.ToHexString(new byte[32])
            })
            .Build();
        return new DockerEngineLogReader(
            client,
            DockerLogProxyOptions.FromConfiguration(configuration),
            TimeProvider.System,
            NullLogger<DockerEngineLogReader>.Instance);
    }

    private static object Container(string id, string project, string service, string name, string state) =>
        new
        {
            Id = id,
            Names = new[] { name },
            State = state,
            Created = 1794384000L,
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = project,
                ["com.docker.compose.service"] = service
            }
        };

    private sealed class FakeDockerEngineHandler(
        object[] containers,
        (string Message, string Timestamp, byte StreamType)[] logs) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(containers), Encoding.UTF8, "application/json")
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/logs", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(BuildFrames(logs)))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] BuildFrames(
            IEnumerable<(string Message, string Timestamp, byte StreamType)> entries)
        {
            using var output = new MemoryStream();
            foreach (var entry in entries)
            {
                var payload = Encoding.UTF8.GetBytes($"{entry.Timestamp} {entry.Message}\n");
                var header = new byte[8];
                header[0] = entry.StreamType;
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)payload.Length);
                output.Write(header);
                output.Write(payload);
            }

            return output.ToArray();
        }
    }
}
