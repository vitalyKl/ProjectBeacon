namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Returns subtasks, sessions, and verdicts for a task.
/// </summary>
public class GetPipelineHandler : ICommandHandler<GetPipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetPipelineHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(GetPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}

