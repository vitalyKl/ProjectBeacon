namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class ClientStoreTests : IDisposable
{
    private readonly string _dir;

    public ClientStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string StorePath => Path.Combine(_dir, "client.json");

    [Fact]
    public void SaveLoad_Roundtrip_PreservesCredentials()
    {
        var device = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = new ClientStore { Url = "http://localhost:5083", Token = "bcd_roundtrip", DeviceId = device };
        store.Save(StorePath);

        var loaded = ClientStore.Load(StorePath);
        Assert.Equal("http://localhost:5083", loaded.Url);
        Assert.Equal("bcd_roundtrip", loaded.Token);
        Assert.Equal(device, loaded.DeviceId);
        Assert.True(loaded.HasCredentials);
    }

    [Fact]
    public void Save_OnWindows_FileDoesNotContainPlaintextToken()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string token = "bcd_supersecret_device_token";
        new ClientStore { Url = "http://localhost:5083", Token = token }.Save(StorePath);

        var raw = File.ReadAllText(StorePath);
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain("bcd_", raw);
    }

    [Fact]
    public void Save_OnUnix_FileHasMode600()
    {
        if (OperatingSystem.IsWindows())
            return;
        new ClientStore { Url = "http://localhost:5083", Token = "bcd_unix" }.Save(StorePath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    [Fact]
    public void Load_LegacyPlaintextFile_ReadsToken_AndSaveMigratesToProtectedFormat()
    {
        var legacy = """{"url":"http://localhost:5083","token":"bcd_legacy","deviceId":"22222222-2222-2222-2222-222222222222"}""";
        File.WriteAllText(StorePath, legacy);

        var loaded = ClientStore.Load(StorePath);
        Assert.Equal("http://localhost:5083", loaded.Url);
        Assert.Equal("bcd_legacy", loaded.Token);
        Assert.True(loaded.HasCredentials);

        loaded.Save(StorePath);
        var raw = File.ReadAllText(StorePath);
        Assert.Contains("\"version\": 2", raw);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain("bcd_legacy", raw);

        var reloaded = ClientStore.Load(StorePath);
        Assert.Equal("bcd_legacy", reloaded.Token);
        Assert.True(reloaded.HasCredentials);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyStore()
    {
        var loaded = ClientStore.Load(Path.Combine(_dir, "nope.json"));
        Assert.False(loaded.HasCredentials);
        Assert.True(ClientStore.NeedsWizard(loaded));
    }

    [Fact]
    public void Load_OnWindows_CorruptedSecret_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var broken = """{"url":"http://localhost:5083","token":"!!!not-a-dpapi-blob!!!","deviceId":"33333333-3333-3333-3333-333333333333","version":2}""";
        File.WriteAllText(StorePath, broken);

        var loaded = ClientStore.Load(StorePath);
        Assert.Equal("http://localhost:5083", loaded.Url);
        Assert.False(loaded.HasCredentials);
    }
}
