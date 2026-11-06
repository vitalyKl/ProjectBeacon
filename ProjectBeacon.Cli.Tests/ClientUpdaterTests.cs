namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Client;

public sealed class ClientUpdaterTests
{
    [Fact]
    public void NewerVersionWithoutPublishedPackage_IsNotReportedAsUpToDateOrInstallable()
    {
        var info = ClientUpdater.CreateUpdateInfo(
            new Version(0, 7, 3, 11),
            new Version(0, 7, 3, 12),
            packageAvailable: false,
            runtimeIdentifier: "win-x64",
            releaseUrl: "https://github.com/vitalyKl/ProjectBeacon/releases/tag/v0.7.3.12");

        Assert.True(info.NewerVersionAvailable);
        Assert.False(info.PackageAvailable);
        Assert.False(info.UpdateAvailable);
        Assert.Equal("0.7.3.11", info.CurrentVersion);
        Assert.Equal("0.7.3.12", info.LatestVersion);
    }

    [Fact]
    public void NewerVersionWithPublishedPackage_IsInstallable()
    {
        var info = ClientUpdater.CreateUpdateInfo(
            new Version(0, 7, 3, 11),
            new Version(0, 7, 3, 12),
            packageAvailable: true,
            runtimeIdentifier: "win-x64",
            releaseUrl: "https://github.com/vitalyKl/ProjectBeacon/releases/tag/v0.7.3.12");

        Assert.True(info.NewerVersionAvailable);
        Assert.True(info.PackageAvailable);
        Assert.True(info.UpdateAvailable);
    }

    [Fact]
    public void SameVersion_IsUpToDateEvenWhenNoPackageExists()
    {
        var info = ClientUpdater.CreateUpdateInfo(
            new Version(0, 7, 3, 12),
            new Version(0, 7, 3, 12),
            packageAvailable: false,
            runtimeIdentifier: "win-x64",
            releaseUrl: "https://github.com/vitalyKl/ProjectBeacon/releases/tag/v0.7.3.12");

        Assert.False(info.NewerVersionAvailable);
        Assert.False(info.PackageAvailable);
        Assert.False(info.UpdateAvailable);
    }
}
