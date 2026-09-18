namespace ProjectBeacon.Infrastructure.Tests;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class LlamaSwapSupervisorTests
{
    [Fact]
    public async Task GetStatusAsync_NoBinPath_IsUnavailable()
    {
        var supervisor = CreateSupervisor(new LlamaSwapOptions { ProjectId = Guid.NewGuid() });
        try
        {
            var status = await supervisor.GetStatusAsync();
            Assert.False(status.Available);
            Assert.False(status.Healthy);
            Assert.Equal("llama-swap binary not configured (set BEACON_LLAMASWAP_BIN).", status.Error);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task GetStatusAsync_NoProjectId_IsUnavailable()
    {
        var supervisor = CreateSupervisor(new LlamaSwapOptions { BinPath = "llama-swap" });
        try
        {
            var status = await supervisor.GetStatusAsync();
            Assert.False(status.Available);
            Assert.False(status.Healthy);
            Assert.Equal("llama-swap project not configured (set BEACON_PROJECT_ID).", status.Error);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task GetStatusAsync_Configured_IsStarting()
    {
        var supervisor = CreateSupervisor(new LlamaSwapOptions
        {
            BinPath = "llama-swap",
            ProjectId = Guid.NewGuid(),
            ConfigPath = Path.Combine(Path.GetTempPath(), "beacon-llamaswap-unused.yaml")
        });
        try
        {
            var status = await supervisor.GetStatusAsync();
            Assert.True(status.Available);
            Assert.False(status.Healthy);
            Assert.Equal("llama-swap is starting.", status.Error);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task ReloadAsync_NotConfigured_ReturnsFalse()
    {
        var supervisor = CreateSupervisor(new LlamaSwapOptions());
        try
        {
            Assert.False(await supervisor.ReloadAsync());
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task ReloadAsync_EmptyRegistry_WritesEmptyModels()
    {
        await WithSqliteAsync(async (connection, factory, projectId, configPath) =>
        {
            var supervisor = CreateSupervisor(new LlamaSwapOptions
            {
                BinPath = "llama-swap",
                ProjectId = projectId,
                ConfigPath = configPath
            }, factory);
            try
            {
                Assert.True(await supervisor.ReloadAsync());
                Assert.Equal("models: {}\n", await File.ReadAllTextAsync(configPath));
            }
            finally
            {
                await supervisor.StopAsync();
            }
        });
    }

    [Fact]
    public async Task ReloadAsync_SeededBackend_WritesGeneratedConfig()
    {
        await WithSqliteAsync(async (connection, factory, projectId, configPath) =>
        {
            using (TenantScope.EnterUnscoped())
            await using (var db = factory.CreateDbContext())
            {
                db.LocalModelBackends.Add(LocalModelBackend.Create(
                    "qwen", ModelBackendType.LlamaCpp, "llama-server -m qwen.gguf", 4096, 300, projectId));
                await db.SaveChangesAsync();
            }

            var supervisor = CreateSupervisor(new LlamaSwapOptions
            {
                BinPath = "llama-swap",
                ProjectId = projectId,
                ConfigPath = configPath
            }, factory);
            try
            {
                Assert.True(await supervisor.ReloadAsync());
                Assert.Equal(
                    "models:\n" +
                    "  qwen:\n" +
                    "    cmd: \"llama-server -m qwen.gguf --ctx-size 4096\"\n" +
                    "    ttl: 300\n",
                    await File.ReadAllTextAsync(configPath));
            }
            finally
            {
                await supervisor.StopAsync();
            }
        });
    }

    [Fact]
    public async Task UnloadAsync_ProxyUp_ReturnsTrue()
    {
        using var server = new FakeLlamaSwapServer();
        var supervisor = CreateSupervisor(new LlamaSwapOptions
        {
            BinPath = "llama-swap",
            ProjectId = Guid.NewGuid(),
            Port = server.Port,
            ConfigPath = Path.Combine(Path.GetTempPath(), "beacon-llamaswap-unused.yaml")
        });
        try
        {
            Assert.True(await supervisor.UnloadAsync());
            Assert.True(server.UnloadCalls > 0);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task UnloadAsync_ProxyDown_ReturnsFalse()
    {
        var supervisor = CreateSupervisor(new LlamaSwapOptions
        {
            BinPath = "llama-swap",
            ProjectId = Guid.NewGuid(),
            Port = UnusedPort(),
            ConfigPath = Path.Combine(Path.GetTempPath(), "beacon-llamaswap-unused.yaml")
        });
        try
        {
            Assert.False(await supervisor.UnloadAsync());
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task PollProxyStatusAsync_FakeHealthRunningMetrics_IsHealthy()
    {
        using var server = new FakeLlamaSwapServer();
        var supervisor = CreateSupervisor(new LlamaSwapOptions
        {
            BinPath = "llama-swap",
            ProjectId = Guid.NewGuid(),
            Port = server.Port,
            ConfigPath = Path.Combine(Path.GetTempPath(), "beacon-llamaswap-unused.yaml")
        });
        try
        {
            var method = typeof(LlamaSwapSupervisor).GetMethod(
                "PollProxyStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = (Task)method!.Invoke(supervisor, [CancellationToken.None])!;
            await task;

            var status = await supervisor.GetStatusAsync();
            Assert.True(status.Available);
            Assert.True(status.Healthy);
            Assert.Equal("test-model", status.LoadedModel);
            Assert.Equal("2.00 MB", status.Memory);
            Assert.Null(status.Error);
            Assert.NotNull(status.LastSwap);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    private static LlamaSwapSupervisor CreateSupervisor(
        LlamaSwapOptions options,
        IDbContextFactory<BeaconDbContext>? factory = null)
        => new(new FixedScopeFactory(factory ?? new MissingDbFactory()), options, NullLogger<LlamaSwapSupervisor>.Instance);

    private static async Task WithSqliteAsync(
        Func<SqliteConnection, IDbContextFactory<BeaconDbContext>, Guid, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-llamaswap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.yaml");
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            var options = new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(connection).Options;
            var factory = new BeaconDbFactory(options, tenant: null);
            var projectId = Guid.NewGuid();
            using (TenantScope.EnterUnscoped())
            await using (var db = new BeaconDbContext(options))
            {
                db.Database.EnsureCreated();
            }

            await body(connection, factory, projectId, configPath);
        }
        finally
        {
            connection.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class MissingDbFactory : IDbContextFactory<BeaconDbContext>
    {
        public BeaconDbContext CreateDbContext()
            => throw new InvalidOperationException("database is not configured for this test.");
    }

    private sealed class FixedScopeFactory : IServiceScopeFactory
    {
        private readonly IDbContextFactory<BeaconDbContext> _factory;
        public FixedScopeFactory(IDbContextFactory<BeaconDbContext> factory) => _factory = factory;
        public IServiceScope CreateScope() => new FixedScope(_factory);
    }

    private sealed class FixedScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }
        public FixedScope(IDbContextFactory<BeaconDbContext> factory)
            => ServiceProvider = new FixedProvider(factory);
        public void Dispose() { }
    }

    private sealed class FixedProvider : IServiceProvider
    {
        private readonly IDbContextFactory<BeaconDbContext> _factory;
        public FixedProvider(IDbContextFactory<BeaconDbContext> factory) => _factory = factory;
        public object? GetService(Type serviceType)
            => serviceType == typeof(IDbContextFactory<BeaconDbContext>) ? _factory : null;
    }

    private sealed class FakeLlamaSwapServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _unloadCalls;

        public FakeLlamaSwapServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public int UnloadCalls => Volatile.Read(ref _unloadCalls);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream);
                byte[] body;
                if (request.Contains("/health", StringComparison.Ordinal))
                    body = "ok"u8.ToArray();
                else if (request.Contains("/running", StringComparison.Ordinal))
                    body = """{"running":[{"model":"test-model","state":"ready"}]}"""u8.ToArray();
                else if (request.Contains("/metrics", StringComparison.Ordinal))
                    body = "process_resident_memory_bytes 2097152\n"u8.ToArray();
                else if (request.Contains("/api/models/unload", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _unloadCalls);
                    body = "unloaded"u8.ToArray();
                }
                else
                {
                    var notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                    await stream.WriteAsync(notFound);
                    return;
                }

                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "
                    + body.Length
                    + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
                if (n == 0)
                    break;
                total += n;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                    return text;
            }
            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
