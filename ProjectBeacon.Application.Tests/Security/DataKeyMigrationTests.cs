namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Security;
using Domain.Entities.Agents;
using Domain.Entities.Identity;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The one-shot v1-to-v2 re-encryption pass: only v1 rows move, failures keep their original
/// ciphertext, and the counts report what actually happened.
/// </summary>
public sealed class DataKeyMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IDisposable _unscoped;

    public DataKeyMigrationTests()
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
    public async Task MixedRows_OnlyV1RowsMigrate()
    {
        var v1User = await AddUser("v1", enabled: false);
        var v2User = await AddUserV2("v2", enabled: false);
        await AddConnection("v1c", v1User.Id, legacy: true);
        await AddConnection("v2c", v2User.Id, legacy: false);
        var v2UserBefore = await Cipher(v2User.Id);

        var result = await Run(TestProtectors.Dev(TestProtectors.LegacyKey));

        Assert.Equal(2, result.Migrated);
        Assert.Equal(0, result.RemainingV1);
        Assert.Equal(0, result.Failed);
        Assert.True(result.LegacyKeyAvailable);

        var migrated = await Cipher(v1User.Id);
        Assert.StartsWith(ISecretProtector.V2Prefix, migrated);
        Assert.Equal("secret-material", TestProtectors.Dev().Open(migrated));
        Assert.Equal(v2UserBefore, await Cipher(v2User.Id));
    }

    [Fact]
    public async Task SecondRun_IsNoOp()
    {
        await AddUser("v1", enabled: false);
        var protector = TestProtectors.Dev(TestProtectors.LegacyKey);

        var first = await Run(protector);
        var second = await Run(protector);

        Assert.Equal(1, first.Migrated);
        Assert.Equal(0, second.Migrated);
        Assert.Equal(0, second.RemainingV1);
        Assert.Equal(0, second.Failed);
    }

    [Fact]
    public async Task FailedRow_KeepsOriginalCiphertext()
    {
        var user = await AddUser("corrupt", enabled: false);
        var original = await Cipher(user.Id);

        var result = await Run(TestProtectors.Dev(TestProtectors.LegacyKey));

        Assert.Equal(0, result.Migrated);
        Assert.Equal(1, result.RemainingV1);
        Assert.Equal(1, result.Failed);
        Assert.Equal(original, await Cipher(user.Id));
    }

    [Fact]
    public async Task NoLegacyKey_V1RowsReportedUnreadable()
    {
        await AddUser("v1", enabled: false);

        var result = await Run(TestProtectors.Prod(writeV2: true));

        Assert.False(result.LegacyKeyAvailable);
        Assert.Equal(0, result.Migrated);
        Assert.Equal(1, result.RemainingV1);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task TotpEnabled_IsPreservedOnMigration()
    {
        var user = await AddUser("enabled", enabled: true);

        var result = await Run(TestProtectors.Dev(TestProtectors.LegacyKey));

        Assert.Equal(1, result.Migrated);
        Assert.Equal(0, result.Failed);
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.True(row.TotpEnabled);
        Assert.StartsWith(ISecretProtector.V2Prefix, row.TotpSecretCipher);
        Assert.Equal("secret-material", TestProtectors.Dev().Open(row.TotpSecretCipher));
    }

    private async Task<DataKeyMigration.Result> Run(ISecretProtector protector)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await DataKeyMigration.ReEncryptAsync(db, protector);
    }

    private async Task<string> Cipher(Guid userId)
    {
        await using var db = _dbFactory.CreateDbContext();
        return (await db.Users.FirstAsync(u => u.Id == userId)).TotpSecretCipher;
    }

    private async Task<User> AddUser(string login, bool enabled)
    {
        var user = User.Create(login, login + "@example.com", "hash");
        // v1 ciphertext under the shared legacy material, unreadable without it.
        user.BeginTotp(SecretBox.Seal("secret-material", TestProtectors.LegacyKey));
        if (enabled)
            user.ConfirmTotp();
        if (login == "corrupt")
            user.UpdateTotpCipher("AAAA-not-a-valid-base64-ciphertext");
        var db = _dbFactory.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return user;
    }

    private async Task<User> AddUserV2(string login, bool enabled)
    {
        var user = User.Create(login, login + "@example.com", "hash");
        user.BeginTotp(TestProtectors.Dev().Seal("secret-material"));
        if (enabled)
            user.ConfirmTotp();
        var db = _dbFactory.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return user;
    }

    private async Task AddConnection(string provider, Guid userId, bool legacy)
    {
        var cipher = legacy
            ? SecretBox.Seal("conn-secret-" + provider, TestProtectors.LegacyKey)
            : TestProtectors.Dev().Seal("conn-secret-" + provider);
        var row = OpenCodeConnection.Create(userId, provider, "model", null, cipher);
        var db = _dbFactory.CreateDbContext();
        db.OpenCodeConnections.Add(row);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
    }
}
