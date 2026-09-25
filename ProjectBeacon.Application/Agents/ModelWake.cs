namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record NudgeModelsCommand(Guid UserId) : ICommand<Result>;

public sealed class NudgeModelsHandler : ICommandHandler<NudgeModelsCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public NudgeModelsHandler(IDbContextFactory<BeaconDbContext> dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result> HandleAsync(NudgeModelsCommand command, CancellationToken ct = default)
    {
        if (command.UserId == Guid.Empty)
            return Result.Failure("Account is not resolved.");
        await using var db = _dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices.Where(d => d.UserId == command.UserId && d.RevokedAt == null).ToListAsync(ct);
        var online = devices.Where(d => d.IsOnline(now)).Select(d => d.Id).ToList();
        foreach (var deviceId in online)
        {
            await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                deviceId, command.UserId, WorkstationCommandKind.ReloadProxy, "{}", null)), ct);
        }

        if (online.Count == 0)
            return Result.Ok();

        var backends = await db.LocalModelBackends.Where(b => b.UserId == command.UserId).ToListAsync(ct);
        var dtos = backends.Select(ModelBackendMappers.ToDto).ToList();
        var preferred = await db.Users.Where(u => u.Id == command.UserId).Select(u => u.ChatModelBackendId).FirstOrDefaultAsync(ct);
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters().Where(r => online.Contains(r.DeviceId)).ToListAsync(ct);
        foreach (var runtime in runtimes)
        {
            var payload = OpencodePayload.BuildApply(
                runtime.LocalRoot, AgentRunMode.Solo, dtos, preferred, null, null, null);
            await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                runtime.DeviceId, command.UserId, WorkstationCommandKind.ApplyOpencode, payload, runtime.ProjectId)), ct);
        }
        return Result.Ok();
    }
}
