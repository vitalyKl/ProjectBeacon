namespace ProjectBeacon.Infrastructure.Tests.Operations;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Operations;

public sealed class HttpPlatformContainerLogClientTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<BeaconDbContext> _options;
    private readonly TenantContext _unscoped;
    private readonly BeaconDbFactory _dbFactory;
    private readonly DatabaseAdminGate _adminGate;
    private readonly User _operator;

    public HttpPlatformContainerLogClientTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(_connection)
            .Options;
        _unscoped = new TenantContext();
        _unscoped.Assign(null, null, unscoped: true);

        using (var seed = new BeaconDbContext(_options, _unscoped))
        {
            seed.Database.EnsureCreated();
            _operator = User.Create(
                "log-operator",
                "log-operator@example.test",
                "test-hash",
                isAdmin: true);
            seed.Users.Add(_operator);
            seed.SaveChanges();
        }

        _dbFactory = new BeaconDbFactory(_options, _unscoped);
        _adminGate = new DatabaseAdminGate(_dbFactory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetLogsAsync_RevalidatesVerifiedOperatorAndAuditsRead()
    {
        var actor = await GetVerifiedOperatorAsync();
        var handler = new DelegateHandler((request, _) =>
        {
            Assert.Equal("/internal/v1/logs/web", request.RequestUri!.AbsolutePath);
            Assert.Contains("tail=2", request.RequestUri.Query);
            Assert.True(request.Headers.Contains("X-Beacon-Ops-Timestamp"));
            Assert.True(request.Headers.Contains("X-Beacon-Ops-Nonce"));
            Assert.True(request.Headers.Contains("X-Beacon-Ops-Signature"));
            return Task.FromResult(SuccessResponse());
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GetLogsAsync(
            actor,
            PlatformLogTarget.Web,
            tail: 2,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal("beacon-alpha-web-1", result.Value!.ContainerName);
        Assert.Equal(2, result.Value.ReturnedLines);

        await using var verify = new BeaconDbContext(_options, _unscoped);
        Assert.True(await verify.AuditEvents.AnyAsync(
            item => item.Action == "platform.container_logs.read",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLogsAsync_WithholdsResponseWhenOperatorPermissionIsRevokedDuringRequest()
    {
        var actor = await GetVerifiedOperatorAsync();
        var handler = new DelegateHandler(async (_, ct) =>
        {
            await ChangeOperatorRoleAsync(OperatorRole.Auditor, ct);
            return SuccessResponse();
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GetLogsAsync(
            actor,
            PlatformLogTarget.Web,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Null(result.Value);

        await using var verify = new BeaconDbContext(_options, _unscoped);
        Assert.False(await verify.AuditEvents.AnyAsync(
            item => item.Action == "platform.container_logs.read",
            TestContext.Current.CancellationToken));
    }

    private HttpPlatformContainerLogClient CreateClient(HttpClient httpClient)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpsAgent:BaseUrl"] = "http://ops-agent:8080/",
                ["OpsAgent:SharedSecret"] = new string('a', 64)
            })
            .Build();

        return new HttpPlatformContainerLogClient(
            httpClient,
            configuration,
            TimeProvider.System,
            NullLogger<HttpPlatformContainerLogClient>.Instance,
            _dbFactory,
            new EfAuditWriter(),
            _adminGate);
    }

    private async Task<AdminActor> GetVerifiedOperatorAsync()
    {
        var result = await _adminGate.RequireAsync(
            HumanOperatorContext(),
            OperatorPermission.OperationsRead,
            ct: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private async Task ChangeOperatorRoleAsync(OperatorRole role, CancellationToken ct)
    {
        await using var db = new BeaconDbContext(_options, _unscoped);
        var user = await db.Users.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == _operator.Id, ct);
        user.AssignOperatorRole(role.ToString());
        await db.SaveChangesAsync(ct);
    }

    private ActorContext HumanOperatorContext() =>
        new(
            ActorType.Human,
            _operator.Id,
            IsAdmin: true,
            TokenId: null,
            DeviceId: null,
            ProjectId: null,
            OrgId: null,
            Capabilities: ApiTokenCapability.None);

    private static HttpResponseMessage SuccessResponse()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new PlatformContainerLogResponse(
            PlatformLogTarget.Web,
            "beacon-alpha-web-1",
            now,
            2,
            [
                new PlatformContainerLogEntry(now, "stdout", "first"),
                new PlatformContainerLogEntry(now, "stderr", "second")
            ]);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload)
        };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
