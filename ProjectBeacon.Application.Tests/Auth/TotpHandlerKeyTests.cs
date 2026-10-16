namespace ProjectBeacon.Application.Tests;

using Application.Auth;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Identity;
using Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// TOTP handlers against v1 (legacy-keyed) and v2 (current-key) ciphertexts, including the
/// no-legacy fail-closed path.
/// </summary>
public sealed class TotpHandlerKeyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IDisposable _unscoped;

    public TotpHandlerKeyTests()
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
    public async Task BeginTotp_StoresV2Cipher_WhenWriteV2()
    {
        var user = await AddUser("begin");
        var protector = TestProtectors.Dev();

        var result = await new BeginTotpHandler(_dbFactory, protector, new RfcTotp())
            .HandleAsync(new BeginTotpCommand(user.Id), TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        await using var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.StartsWith(ISecretProtector.V2Prefix, row.TotpSecretCipher);
        Assert.False(row.TotpEnabled);
    }

    [Fact]
    public async Task GetTotpStatus_ReadsPendingV1Cipher_WithLegacyKey()
    {
        const string secret = "JBSWY3DPEHPK3PXP";
        var user = await AddUser("v1pending", TestProtectors.LegacyKey);

        var result = await new GetTotpStatusHandler(_dbFactory, TestProtectors.Dev(TestProtectors.LegacyKey), new RfcTotp())
            .HandleAsync(new GetTotpStatusCommand(user.Id), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Value!.Pending);
        Assert.False(result.Value.Enabled);
        Assert.Equal(secret, result.Value.Secret);
        Assert.StartsWith("otpauth://totp/", result.Value.OtpAuthUri);
    }

    [Fact]
    public async Task GetTotpStatus_V1Cipher_WithoutLegacyKey_DegradesToNotPending()
    {
        var user = await AddUser("v1nolegacy", TestProtectors.LegacyKey);

        var result = await new GetTotpStatusHandler(_dbFactory, TestProtectors.Dev(), new RfcTotp())
            .HandleAsync(new GetTotpStatusCommand(user.Id), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.Value!.Pending);
        Assert.Null(result.Value.Secret);
    }

    [Fact]
    public async Task ConfirmTotp_V1Cipher_WithLegacyKey_Enablestotp()
    {
        var user = await AddUser("v1confirm", TestProtectors.LegacyKey);
        var protector = TestProtectors.Dev(TestProtectors.LegacyKey);
        var handler = new ConfirmTotpHandler(_dbFactory, protector, new RfcTotp());

        var code = CurrentCode(await PendingSecret(user.Id, protector));
        var result = await handler.HandleAsync(new ConfirmTotpCommand(new ConfirmTotpRequest(user.Id, code)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.True(row.TotpEnabled);
    }

    [Fact]
    public async Task ConfirmTotp_V1Cipher_WithoutLegacyKey_FailsToRead()
    {
        var user = await AddUser("v1confirm-nolegacy", TestProtectors.LegacyKey);

        var result = await new ConfirmTotpHandler(_dbFactory, TestProtectors.Dev(), new RfcTotp())
            .HandleAsync(new ConfirmTotpCommand(new ConfirmTotpRequest(user.Id, "123456")),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cannot be read", result.Error);
    }

    [Fact]
    public async Task DisableTotp_V2Cipher_DisablesWithValidCode()
    {
        var user = await AddEnabledUser("v2disable");
        var protector = TestProtectors.Dev(TestProtectors.LegacyKey);
        var handler = new DisableTotpHandler(_dbFactory, protector, new RfcTotp());

        var code = CurrentCode(await PendingSecret(user.Id, protector));
        var result = await handler.HandleAsync(new DisableTotpCommand(new DisableTotpRequest(user.Id, code)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.False(row.TotpEnabled);
        Assert.Equal(string.Empty, row.TotpSecretCipher);
    }

    [Fact]
    public async Task TotpGate_V2Enabled_ComparesCode()
    {
        var user = await AddEnabledUser("gate-v2");
        var protector = TestProtectors.Dev(TestProtectors.LegacyKey);

        var ok = await TotpGate.RequireAsync(
            _dbFactory.CreateDbContext(), user.Id, CurrentCode(await PendingSecret(user.Id, protector)), protector, new RfcTotp(),
            TestContext.Current.CancellationToken);
        Assert.True(ok.Success);

        var denied = await TotpGate.RequireAsync(
            _dbFactory.CreateDbContext(), user.Id, "000000", protector, new RfcTotp(),
            TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
    }

    [Fact]
    public async Task TotpGate_CorruptedV2Cipher_FailsToRead()
    {
        var user = await AddEnabledUser("gate-corrupt");
        var protector = TestProtectors.Dev();
        var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        var chars = protector.Seal("JBSWY3DPEHPK3PXP").ToCharArray();
        chars[ISecretProtector.V2Prefix.Length] = chars[ISecretProtector.V2Prefix.Length] == 'A' ? 'B' : 'A';
        row.UpdateTotpCipher(new string(chars));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await db.DisposeAsync();

        var result = await TotpGate.RequireAsync(
            _dbFactory.CreateDbContext(), user.Id, "123456", TestProtectors.Dev(), new RfcTotp(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cannot be read", result.Error);
    }

    private async Task<User> AddUser(string login, string? legacyKey = null)
    {
        var user = User.Create(login, login + "@example.com", "hash");
        user.BeginTotp(SecretBox.Seal("JBSWY3DPEHPK3PXP", legacyKey ?? TestProtectors.LegacyKey));
        var db = _dbFactory.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return user;
    }

    private async Task<User> AddEnabledUser(string login)
    {
        var user = await AddUser(login);
        var db = _dbFactory.CreateDbContext();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        row.ConfirmTotp();
        await db.SaveChangesAsync();
        await db.DisposeAsync();
        return user;
    }

    private async Task<string> PendingSecret(Guid userId, ISecretProtector protector)
    {
        var db = _dbFactory.CreateDbContext();
        var row = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        await db.DisposeAsync();
        return protector.Open(row.TotpSecretCipher);
    }

    private static string CurrentCode(string secret) =>
        Totp.Hotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
}
