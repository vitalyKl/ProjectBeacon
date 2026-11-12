namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Tasks;
using Domain.Common;
using Domain.Enums;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Microsoft.EntityFrameworkCore;

public sealed class PipelineFormDeviceQueryTests
{
    [Fact]
    public async Task ActiveDevicePredicate_TranslatesUsingMappedRevokedAtColumn()
    {
        var (connection, db, unscoped) = HandlerSqlite.Open();
        using (connection)
        using (db)
        using (unscoped)
        {
            var user = User.Create("form-query-user", "form-query-user@example.test", "test-hash");
            var active = DaemonDevice.Create("active", user.Id, "active-fingerprint", "active-token-hash", "active");
            var revoked = DaemonDevice.Create("revoked", user.Id, "revoked-fingerprint", "revoked-token-hash", "revoked");
            revoked.Revoke();

            db.Users.Add(user);
            db.DaemonDevices.AddRange(active, revoked);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var matches = await db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == active.Id
                    && device.UserId == user.Id
                    && device.RevokedAt == null)
                .Select(device => device.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Single(matches);
            Assert.Equal(active.Id, matches[0]);
        }
    }

    [Fact]
    public async Task ReplyPipelineForm_RejectsApiTokenEvenWhenItCarriesAUserId()
    {
        var actor = new ActorContext(
            ActorType.ApiToken, Guid.NewGuid(), false, Guid.NewGuid(), null,
            null, null, ApiTokenCapability.TaskRead);
        var handler = new ReplyPipelineFormHandler(null!, null!);

        var result = await handler.HandleAsync(new ReplyPipelineFormCommand(
            new ReplyPipelineFormRequest(
                Guid.NewGuid(), Guid.NewGuid(), "question", "{}"),
            actor), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task ListWaitingPipelineTasks_RejectsAnotherMembersUserId()
    {
        var actorUserId = Guid.NewGuid();
        var requestedUserId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.Human, actorUserId, false, null, null,
            null, null, ApiTokenCapability.None);
        var handler = new ListWaitingPipelineTasksHandler(null!);

        var result = await handler.HandleAsync(new ListWaitingPipelineTasksCommand(
            new ListWaitingPipelineTasksRequest(requestedUserId), actor),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task ListWaitingPipelineTasks_RejectsApiTokenWithCreatorUserId()
    {
        var userId = Guid.NewGuid();
        var actor = new ActorContext(
            ActorType.ApiToken, userId, false, Guid.NewGuid(), null,
            null, null, ApiTokenCapability.TaskRead);
        var handler = new ListWaitingPipelineTasksHandler(null!);

        var result = await handler.HandleAsync(new ListWaitingPipelineTasksCommand(
            new ListWaitingPipelineTasksRequest(userId), actor),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
    }

}
