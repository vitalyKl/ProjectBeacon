namespace ProjectBeacon.Infrastructure.Tests.Operations;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.OpsAgent;

public sealed class DockerJsonLogReaderTests
{
    [Fact]
    public async Task ReadAsync_ReturnsOnlyTheRequestedComposeProjectAndService()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var webId = new string('a', 64);
            var foreignId = new string('b', 64);
            var workerId = new string('c', 64);
            CreateContainer(root, webId, "beacon-alpha", "web", "/beacon-alpha-web-1");
            CreateContainer(root, foreignId, "other-project", "web", "/other-web-1");
            CreateContainer(root, workerId, "beacon-alpha", "worker", "/beacon-alpha-worker-1");

            await WriteLogsAsync(root, webId,
                ("web-one", "2026-11-11T08:00:00.0000000Z"),
                ("web-two", "2026-11-11T08:00:01.0000000Z"),
                ("web-three", "2026-11-11T08:00:02.0000000Z"));
            await WriteLogsAsync(root, foreignId,
                ("FOREIGN-MUST-NOT-RETURN", "2026-11-11T08:00:03.0000000Z"));
            await WriteLogsAsync(root, workerId,
                ("WORKER-MUST-NOT-RETURN", "2026-11-11T08:00:04.0000000Z"));

            var reader = CreateReader(root);
            var response = await reader.ReadAsync(PlatformLogTarget.Web, 2, TestContext.Current.CancellationToken);

            Assert.NotNull(response);
            Assert.Equal(PlatformLogTarget.Web, response.Service);
            Assert.Equal("/beacon-alpha-web-1", response.ContainerName);
            Assert.Equal(2, response.ReturnedLines);
            Assert.Equal(new[] { "web-two", "web-three" }, response.Entries.Select(entry => entry.Message));
            Assert.DoesNotContain(response.Entries, entry =>
                entry.Message.Contains("FOREIGN", StringComparison.Ordinal)
                || entry.Message.Contains("WORKER", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_CombinesCurrentAndRotatedFilesInTimestampOrder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var id = new string('d', 64);
            CreateContainer(root, id, "beacon-alpha", "worker", "/beacon-alpha-worker-1");
            await WriteLogsAsync(root, id,
                ("current-one", "2026-11-11T08:00:02.0000000Z"),
                ("current-two", "2026-11-11T08:00:03.0000000Z"));
            await WriteLogsAsync(root, id,
                ("rotated-one", "2026-11-11T08:00:00.0000000Z"),
                ("rotated-two", "2026-11-11T08:00:01.0000000Z"),
                suffix: ".1");

            var reader = CreateReader(root);
            var response = await reader.ReadAsync(PlatformLogTarget.Worker, 3, TestContext.Current.CancellationToken);

            Assert.NotNull(response);
            Assert.Equal(new[] { "rotated-two", "current-one", "current-two" },
                response.Entries.Select(entry => entry.Message));
            Assert.All(response.Entries, entry => Assert.Equal("stdout", entry.Stream));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullForContainersFromAnotherComposeProject()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var id = new string('e', 64);
            CreateContainer(root, id, "not-beacon-alpha", "web", "/other-web");
            await WriteLogsAsync(root, id,
                ("foreign-log", "2026-11-11T08:00:00.0000000Z"));

            var reader = CreateReader(root);
            var response = await reader.ReadAsync(PlatformLogTarget.Web, 100, TestContext.Current.CancellationToken);

            Assert.Null(response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static DockerJsonLogReader CreateReader(string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPS_AGENT_LOG_ROOT"] = root,
                ["OPS_AGENT_COMPOSE_PROJECT"] = "beacon-alpha",
                ["OPS_AGENT_SHARED_SECRET"] = Convert.ToHexString(new byte[32])
            })
            .Build();
        return new DockerJsonLogReader(
            OpsAgentOptions.FromConfiguration(configuration),
            TimeProvider.System,
            NullLogger<DockerJsonLogReader>.Instance);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "beacon-opsagent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateContainer(
        string root,
        string id,
        string project,
        string service,
        string name)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var metadata = new
        {
            Name = name,
            Config = new
            {
                Labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = project,
                    ["com.docker.compose.service"] = service
                }
            },
            State = new { Running = true }
        };
        File.WriteAllText(
            Path.Combine(directory, "config.v2.json"),
            JsonSerializer.Serialize(metadata));
    }

    private static async Task WriteLogsAsync(
        string root,
        string id,
        params (string Message, string Timestamp)[] values) =>
        await WriteLogsAsync(root, id, values, string.Empty);

    private static async Task WriteLogsAsync(
        string root,
        string id,
        (string Message, string Timestamp)[] values,
        string suffix)
    {
        var path = Path.Combine(root, id, id + "-json.log" + suffix);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        foreach (var (message, timestamp) in values)
        {
            var line = JsonSerializer.Serialize(new
            {
                log = message + "\n",
                stream = "stdout",
                time = timestamp
            });
            await writer.WriteLineAsync(line);
        }
    }
}
