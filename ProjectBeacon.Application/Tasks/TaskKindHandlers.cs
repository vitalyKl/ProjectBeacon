namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record TaskKindPhaseDto(
    Guid Id,
    int SortOrder,
    string Key,
    string Title,
    string Instruction,
    bool FanOut,
    Guid? ModelBackendId);

public record TaskKindDto(
    Guid Id,
    string Name,
    bool IsBuiltIn,
    Guid? DecisionBackendId,
    Guid? WorkerBackendId,
    IReadOnlyList<TaskKindPhaseDto> Phases);

public record TaskPhaseDto(
    Guid Id,
    Guid TaskId,
    int SortOrder,
    string Key,
    string Title,
    string Instruction,
    bool FanOut,
    Guid? ModelBackendId,
    TaskPhaseStatus Status);

public record TaskKindPhaseInput(string? Key, string Title, string? Instruction, bool FanOut, Guid? ModelBackendId);

public record ListTaskKindsCommand(Guid UserId) : ICommand<Result<IList<TaskKindDto>>>;

public record SaveTaskKindRequest(
    Guid? Id,
    Guid UserId,
    string Name,
    Guid? DecisionBackendId,
    Guid? WorkerBackendId,
    IReadOnlyList<TaskKindPhaseInput> Phases);

public record SaveTaskKindCommand(SaveTaskKindRequest Request) : ICommand<Result<TaskKindDto>>;

public record DeleteTaskKindRequest(Guid Id, Guid UserId);

public record DeleteTaskKindCommand(DeleteTaskKindRequest Request) : ICommand<Result>;

public record ResetBuiltInTaskKindCommand(Guid UserId) : ICommand<Result<TaskKindDto>>;

public record ListTaskPhasesCommand(Guid TaskId) : ICommand<Result<IList<TaskPhaseDto>>>;

public record SetTaskPhaseModelRequest(Guid PhaseId, Guid? ModelBackendId);

public record SetTaskPhaseModelCommand(SetTaskPhaseModelRequest Request) : ICommand<Result<TaskPhaseDto>>;

public static class BuiltInTaskKind
{
    public const string Name = "Default";

    public static readonly (string Key, string Title)[] Phases =
    [
        ("understand", "Understand the task"),
        ("do", "Do the work"),
        ("check", "Check the result")
    ];

    public static async Task<TaskKind> EnsureAsync(BeaconDbContext db, Guid userId, CancellationToken ct)
    {
        var existing = await db.TaskKinds.FirstOrDefaultAsync(k => k.UserId == userId && k.IsBuiltIn, ct);
        if (existing is not null)
            return existing;

        var kind = TaskKind.Create(userId, Name, true, null, null);
        db.TaskKinds.Add(kind);
        for (var i = 0; i < Phases.Length; i++)
        {
            db.TaskKindPhases.Add(TaskKindPhase.Create(
                kind.Id, i, Phases[i].Key, Phases[i].Title, null, false, null));
        }
        await db.SaveChangesAsync(ct);
        return kind;
    }
}

public sealed class ListTaskKindsHandler : ICommandHandler<ListTaskKindsCommand, Result<IList<TaskKindDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListTaskKindsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskKindDto>>> HandleAsync(ListTaskKindsCommand command, CancellationToken ct = default)
    {
        if (command.UserId == Guid.Empty)
            return Result.Failure<IList<TaskKindDto>>("Account is not resolved.");
        await using var db = _dbFactory.CreateDbContext();
        await BuiltInTaskKind.EnsureAsync(db, command.UserId, ct);
        return Result.Ok(await TaskKindReader.ListAsync(db, command.UserId, ct));
    }
}

public sealed class SaveTaskKindHandler : ICommandHandler<SaveTaskKindCommand, Result<TaskKindDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SaveTaskKindHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskKindDto>> HandleAsync(SaveTaskKindCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var name = request.Name?.Trim() ?? string.Empty;
        if (request.UserId == Guid.Empty)
            return Result.Failure<TaskKindDto>("Account is not resolved.");
        if (request.Phases is null || request.Phases.Count == 0)
            return Result.Failure<TaskKindDto>("A task kind needs at least one phase.");
        if (request.Phases.Any(p => string.IsNullOrWhiteSpace(p.Title) || p.Title.Trim().Length > 200))
            return Result.Failure<TaskKindDto>("Each phase needs a title (max 200 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var modelIds = new[] { request.DecisionBackendId, request.WorkerBackendId }
            .Concat(request.Phases.Select(p => p.ModelBackendId))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (modelIds.Count > 0)
        {
            var owned = await db.LocalModelBackends.CountAsync(b => modelIds.Contains(b.Id) && b.UserId == request.UserId, ct);
            if (owned != modelIds.Count)
                return Result.Failure<TaskKindDto>("Model backend not found.");
        }

        TaskKind kind;
        if (request.Id is { } id)
        {
            var found = await db.TaskKinds.FirstOrDefaultAsync(k => k.Id == id && k.UserId == request.UserId, ct);
            if (found is null)
                return Result.Failure<TaskKindDto>("Task kind not found.");
            kind = found;
            if (!kind.IsBuiltIn && (name.Length == 0 || name.Length > 200))
                return Result.Failure<TaskKindDto>("Name is required (max 200 characters).");
            kind.Update(name, request.DecisionBackendId, request.WorkerBackendId);
            var old = await db.TaskKindPhases.Where(p => p.TaskKindId == kind.Id).ToListAsync(ct);
            db.TaskKindPhases.RemoveRange(old);
        }
        else
        {
            if (name.Length == 0 || name.Length > 200)
                return Result.Failure<TaskKindDto>("Name is required (max 200 characters).");
            kind = TaskKind.Create(request.UserId, name, false, request.DecisionBackendId, request.WorkerBackendId);
            db.TaskKinds.Add(kind);
        }

        for (var i = 0; i < request.Phases.Count; i++)
        {
            var phase = request.Phases[i];
            var key = kind.IsBuiltIn && i < BuiltInTaskKind.Phases.Length
                ? BuiltInTaskKind.Phases[i].Key
                : phase.Key;
            db.TaskKindPhases.Add(TaskKindPhase.Create(
                kind.Id, i, key, phase.Title, phase.Instruction, phase.FanOut, phase.ModelBackendId));
        }

        await db.SaveChangesAsync(ct);
        var list = await TaskKindReader.ListAsync(db, request.UserId, ct);
        return Result.Ok(list.First(k => k.Id == kind.Id));
    }
}

public sealed class DeleteTaskKindHandler : ICommandHandler<DeleteTaskKindCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteTaskKindHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteTaskKindCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var kind = await db.TaskKinds.FirstOrDefaultAsync(k => k.Id == command.Request.Id && k.UserId == command.Request.UserId, ct);
        if (kind is null)
            return Result.Failure("Task kind not found.");
        if (kind.IsBuiltIn)
            return Result.Failure("The built-in task kind cannot be deleted.");
        var phases = await db.TaskKindPhases.Where(p => p.TaskKindId == kind.Id).ToListAsync(ct);
        db.TaskKindPhases.RemoveRange(phases);
        db.TaskKinds.Remove(kind);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class ResetBuiltInTaskKindHandler : ICommandHandler<ResetBuiltInTaskKindCommand, Result<TaskKindDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ResetBuiltInTaskKindHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskKindDto>> HandleAsync(ResetBuiltInTaskKindCommand command, CancellationToken ct = default)
    {
        if (command.UserId == Guid.Empty)
            return Result.Failure<TaskKindDto>("Account is not resolved.");
        await using var db = _dbFactory.CreateDbContext();
        var kind = await BuiltInTaskKind.EnsureAsync(db, command.UserId, ct);
        kind.Update(BuiltInTaskKind.Name, null, null);
        var old = await db.TaskKindPhases.Where(p => p.TaskKindId == kind.Id).ToListAsync(ct);
        db.TaskKindPhases.RemoveRange(old);
        for (var i = 0; i < BuiltInTaskKind.Phases.Length; i++)
        {
            db.TaskKindPhases.Add(TaskKindPhase.Create(
                kind.Id, i, BuiltInTaskKind.Phases[i].Key, BuiltInTaskKind.Phases[i].Title, null, false, null));
        }
        await db.SaveChangesAsync(ct);
        var list = await TaskKindReader.ListAsync(db, command.UserId, ct);
        return Result.Ok(list.First(k => k.Id == kind.Id));
    }
}

public sealed class ListTaskPhasesHandler : ICommandHandler<ListTaskPhasesCommand, Result<IList<TaskPhaseDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListTaskPhasesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskPhaseDto>>> HandleAsync(ListTaskPhasesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var phases = await db.TaskPhases.Where(p => p.TaskId == command.TaskId).OrderBy(p => p.SortOrder).ToListAsync(ct);
        return Result.Ok<IList<TaskPhaseDto>>(phases.Select(TaskKindReader.ToDto).ToList());
    }
}

public sealed class SetTaskPhaseModelHandler : ICommandHandler<SetTaskPhaseModelCommand, Result<TaskPhaseDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SetTaskPhaseModelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskPhaseDto>> HandleAsync(SetTaskPhaseModelCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var phase = await db.TaskPhases.FirstOrDefaultAsync(p => p.Id == command.Request.PhaseId, ct);
        if (phase is null)
            return Result.Failure<TaskPhaseDto>("Phase not found.");
        phase.SetModel(command.Request.ModelBackendId);
        await db.SaveChangesAsync(ct);
        return Result.Ok(TaskKindReader.ToDto(phase));
    }
}

internal static class TaskKindReader
{
    public static async Task<IList<TaskKindDto>> ListAsync(BeaconDbContext db, Guid userId, CancellationToken ct)
    {
        var kinds = await db.TaskKinds.Where(k => k.UserId == userId).OrderByDescending(k => k.IsBuiltIn).ThenBy(k => k.Name).ToListAsync(ct);
        var ids = kinds.Select(k => k.Id).ToList();
        var phases = await db.TaskKindPhases.Where(p => ids.Contains(p.TaskKindId)).OrderBy(p => p.SortOrder).ToListAsync(ct);
        return kinds.Select(k => new TaskKindDto(
            k.Id,
            k.Name,
            k.IsBuiltIn,
            k.DecisionBackendId,
            k.WorkerBackendId,
            phases.Where(p => p.TaskKindId == k.Id).Select(p => new TaskKindPhaseDto(
                p.Id, p.SortOrder, p.Key, p.Title, p.Instruction, p.FanOut, p.ModelBackendId)).ToList())).ToList();
    }

    public static TaskPhaseDto ToDto(TaskPhase phase) =>
        new(phase.Id, phase.TaskId, phase.SortOrder, phase.Key, phase.Title, phase.Instruction, phase.FanOut, phase.ModelBackendId, phase.Status);
}

public static class TaskKindCopy
{
    public static async Task CopyOntoTaskAsync(BeaconDbContext db, TaskItem task, Guid userId, Guid? kindId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return;
        var kind = kindId is { } id
            ? await db.TaskKinds.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, ct)
            : await BuiltInTaskKind.EnsureAsync(db, userId, ct);
        if (kind is null)
            kind = await BuiltInTaskKind.EnsureAsync(db, userId, ct);
        var phases = await db.TaskKindPhases.Where(p => p.TaskKindId == kind.Id).OrderBy(p => p.SortOrder).ToListAsync(ct);
        foreach (var phase in phases)
        {
            var model = phase.ModelBackendId
                ?? (phase.FanOut ? kind.WorkerBackendId ?? kind.DecisionBackendId : kind.DecisionBackendId);
            db.TaskPhases.Add(TaskPhase.CopyFrom(phase, task.Id, task.ProjectId, model));
        }
    }
}
