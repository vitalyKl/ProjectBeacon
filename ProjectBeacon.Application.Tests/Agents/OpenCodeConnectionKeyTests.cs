namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Application.Devices;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Agents;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// OpenCode connection save/read against v1 (legacy-keyed) and v2 (current-key) ciphertexts.
/// </summary>
public sealed class OpenCodeConnectionKeyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IDisposable _unscoped;

    public OpenCodeConnectionKeyTests()
    {
        (_connection, _, _unscoped) = HandlerSqlite.Open();
        _dbFactory = HandlerSqlite.Factory(_connection);
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Save_WithWriteV2_StoresV2Cipher()
    {
        var user = await AddUser("save-v2");

        var result = await new SaveOpenCodeConnectionHandler(_dbFactory, Nudge(), TestProtectors.Dev())
            .HandleAsync(new SaveOpenCodeConnectionCommand(
                new SaveOpenCodeConnectionRequest(user.Id, "xai", "grok-code-fast", null, "sk-v2-key")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Value!.HasApiKey);

        await using var db = _dbFactory.CreateDbContext();
        var row = await db.OpenCodeConnections.FirstAsync(c => c.UserId == user.Id, TestContext.Current.CancellationToken);
        Assert.StartsWith(ISecretProtector.V2Prefix, row.ApiKeyCipher);
    }

    [Fact]
    public async Task Save_WithWriteV1Off_StoresLegacyCipher()
    {
        var user = await AddUser("save-v1");

        var result = await new SaveOpenCodeConnectionHandler(_dbFactory, Nudge(), TestProtectors.Dev(TestProtectors.LegacyKey, writeV2: false))
            .HandleAsync(new SaveOpenCodeConnectionCommand(
                new SaveOpenCodeConnectionRequest(user.Id, "xai", "grok-code-fast", null, "sk-v1-key")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        await using var db = _dbFactory.CreateDbContext();
        var row = await db.OpenCodeConnections.FirstAsync(c => c.UserId == user.Id, TestContext.Current.CancellationToken);
        Assert.False(row.ApiKeyCipher.StartsWith(ISecretProtector.V2Prefix));
        Assert.Equal("sk-v1-key", TestProtectors.Dev(TestProtectors.LegacyKey).Open(row.ApiKeyCipher));
    }

    [Fact]
    public async Task DeviceConnections_ReturnsV2Key_ToOwnerDevice()
    {
        var user = await AddUser("dev-v2");
        await SaveKey(user.Id, "xai", "sk-v2-key");
        var device = await AddDevice(user.Id);

        var result = await new DeviceOpenCodeConnectionsHandler(_dbFactory, TestProtectors.Dev())
            .HandleAsync(new DeviceOpenCodeConnectionsCommand(device), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var row = result.Value!.First(c => c.ProviderId == "xai");
        Assert.Equal("sk-v2-key", row.ApiKey);
    }

    [Fact]
    public async Task DeviceConnections_ReturnsV1Key_ViaLegacyKey()
    {
        var user = await AddUser("dev-v1");
        await AddConnection(user.Id, "openai", "gpt-x", SecretBox.Seal("sk-v1-key", TestProtectors.LegacyKey));
        var device = await AddDevice(user.Id);

        var result = await new DeviceOpenCodeConnectionsHandler(_dbFactory, TestProtectors.Dev(TestProtectors.LegacyKey))
            .HandleAsync(new DeviceOpenCodeConnectionsCommand(device), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("sk-v1-key", result.Value!.First().ApiKey);
    }

    [Fact]
    public async Task DeviceConnections_V1WithoutLegacyKey_ReturnsNullKey()
    {
        var user = await AddUser("dev-v1-nolegacy");
        await AddConnection(user.Id, "openai", "gpt-x", SecretBox.Seal("sk-v1-key", TestProtectors.LegacyKey));
        var device = await AddDevice(user.Id);

        var result = await new DeviceOpenCodeConnectionsHandler(_dbFactory, TestProtectors.Dev())
            .HandleAsync(new DeviceOpenCodeConnectionsCommand(device), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Value!.First().ApiKey);
    }

    private async Task<User> AddUser(string login)
    {
        var user = User.Create(login, login + "@example.com", "hash");
        var db = _dbFactory.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return user;
    }

    private async Task SaveKey(Guid userId, string provider, string key)
    {
        var handler = new SaveOpenCodeConnectionHandler(_dbFactory, Nudge(), TestProtectors.Dev());
        var result = await handler.HandleAsync(new SaveOpenCodeConnectionCommand(
            new SaveOpenCodeConnectionRequest(userId, provider, "model", null, key)));
        Assert.True(result.Success, result.Error);
    }

    private async Task AddConnection(Guid userId, string provider, string model, string cipher)
    {
        var row = OpenCodeConnection.Create(userId, provider, model, null, cipher);
        var db = _dbFactory.CreateDbContext();
        db.OpenCodeConnections.Add(row);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
    }

    private async Task<Guid> AddDevice(Guid userId)
    {
        var device = DaemonDevice.Create("device", userId, "fingerprint", "token-hash", "bcd_test");
        var db = _dbFactory.CreateDbContext();
        db.DaemonDevices.Add(device);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return device.Id;
    }

    private NudgeOpenCodeHandler Nudge() => new(_dbFactory, new EnqueueCommandHandler(_dbFactory));
}
