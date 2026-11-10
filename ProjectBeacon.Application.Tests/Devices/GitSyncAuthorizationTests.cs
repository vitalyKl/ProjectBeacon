namespace ProjectBeacon.Application.Tests.Devices;

using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Enums;
using System.Text.Json;
using Xunit;

public sealed class GitSyncAuthorizationTests
{

    [Fact]
    public void RuntimeSummary_ReportsCapabilityOfTheAttachedDeviceWithoutExposingProbeJson()
    {
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var device = DaemonDevice.Create("runtime-device", userId, "fingerprint", "token-hash", "bcd_test");
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        var runtime = ProjectRuntime.Create(projectId, device.Id, Path.GetTempPath());

        var summary = AttachRuntimeHandler.MapSummary(runtime, device, DateTime.UtcNow);

        Assert.True(summary.GitSyncSupported);
        Assert.True(summary.DeviceOnline);
        Assert.DoesNotContain("capabilities", JsonSerializer.Serialize(summary), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericDeviceCommandEnqueue_RejectsGitSyncOutsideProjectAdminHandler()
    {
        // The guard must run before the database is touched. The generic device-command
        // endpoint is intentionally not an alternative path around project Owner/Admin
        // authorization enforced by EnqueueProjectGitSyncHandler.
        var handler = new EnqueueCommandHandler(dbFactory: null!);
        var projectId = Guid.NewGuid();

        var result = await handler.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkstationCommandKind.GitSync,
                """{"projectId":"00000000-0000-0000-0000-000000000001","mode":"Status"}""",
                projectId)));

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Contains("project GitSync endpoint", result.Error, StringComparison.Ordinal);
    }
}
