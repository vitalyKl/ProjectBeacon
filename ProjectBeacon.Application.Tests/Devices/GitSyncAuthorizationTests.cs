namespace ProjectBeacon.Application.Tests.Devices;

using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Domain.Enums;
using Xunit;

public sealed class GitSyncAuthorizationTests
{
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
