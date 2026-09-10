namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Data;

public sealed class EnvFileTests
{
    [Fact]
    public void Load_AppliesUnsetKeys_FromRepoDotEnv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("BEACON_ENVFILE_TEST");
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable("BEACON_ENVFILE_TEST", null);
            File.WriteAllText(Path.Combine(dir, ".env"), "BEACON_ENVFILE_TEST=loaded-from-file\n");
            Directory.SetCurrentDirectory(dir);
            EnvFile.Load();
            Assert.Equal("loaded-from-file", Environment.GetEnvironmentVariable("BEACON_ENVFILE_TEST"));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
            Environment.SetEnvironmentVariable("BEACON_ENVFILE_TEST", previous);
            Directory.Delete(dir, true);
        }
    }
}
