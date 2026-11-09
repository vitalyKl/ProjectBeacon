namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Common;

public sealed class WorkstationCapabilitiesTests
{
    [Fact]
    public void SupportsGitSync_RequiresExplicitCapabilityAdvertisement()
    {
        Assert.False(WorkstationCapabilities.Supports(null, WorkstationCapabilities.GitSyncV1));
        Assert.False(WorkstationCapabilities.Supports("{}", WorkstationCapabilities.GitSyncV1));
        Assert.False(WorkstationCapabilities.Supports(
            """{"capabilities":["other-feature"]}""",
            WorkstationCapabilities.GitSyncV1));
        Assert.False(WorkstationCapabilities.Supports(
            """{"capabilities":"git-sync-v1"}""",
            WorkstationCapabilities.GitSyncV1));
        Assert.False(WorkstationCapabilities.Supports(
            """{"capabilities":["git-sync-v1"]""",
            WorkstationCapabilities.GitSyncV1));

        Assert.True(WorkstationCapabilities.Supports(
            """{"capabilities":["git-sync-v1"]}""",
            WorkstationCapabilities.GitSyncV1));
    }
}
