namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Auth;
using Application.Common;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

public class CreateDeviceHandler : ICommandHandler<CreateDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector? _secrets;
    private readonly ITotp? _totp;

    public CreateDeviceHandler(IBeaconDbFactory dbFactory, ISecretProtector? secrets = null, ITotp? totp = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

    public async Task<Result<DaemonDeviceDto>> HandleAsync(CreateDeviceCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            return Result.Failure<DaemonDeviceDto>("Name is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Trim().Length > 200)
            return Result.Failure<DaemonDeviceDto>("Fingerprint is required (max 200 characters).");
        if (request.UserId == Guid.Empty)
            return Result.Failure<DaemonDeviceDto>("User is required.");

        await using var db = _dbFactory.CreateDbContext();
        var userExists = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == request.UserId, ct);
        if (!userExists)
            return Result.Failure<DaemonDeviceDto>("User not found.");
        var gate = await TotpGate.RequireAsync(db, request.UserId, request.TotpCode, _secrets, _totp, ct);
        if (!gate.Success)
            return Result.Failure<DaemonDeviceDto>(gate.Error ?? "Authenticator code is required.");

        var token = DeviceToken.Generate();
        var hash = DeviceToken.Hash(token);
        var prefix = DeviceToken.TokenPrefixOf(token);

        var existing = await db.DaemonDevices
            .FirstOrDefaultAsync(d => d.UserId == request.UserId && d.Fingerprint == request.Fingerprint.Trim() && d.RevokedAt == null, ct);

        if (existing is not null)
        {
            existing.RotateToken(hash, prefix);
            existing.Rename(request.Name);
            await db.SaveChangesAsync(ct);
            return Result.Ok(MapDevice(existing, token, DateTime.UtcNow));
        }

        var device = DaemonDevice.Create(request.Name, request.UserId, request.Fingerprint, hash, prefix);
        db.DaemonDevices.Add(device);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapDevice(device, token, DateTime.UtcNow));
    }

    internal static DaemonDeviceDto MapDevice(DaemonDevice device, string? token, DateTime utcNow) =>
        new(device.Id, device.Name, device.UserId, device.Fingerprint, device.TokenPrefix, token,
            device.LastHeartbeatAt, device.IsOnline(utcNow), device.ProbeJson, device.WorkstationJson,
            device.RevokedAt, device.CreatedAt, device.DesiredRevision, device.AppliedRevision);
}

public class ListDevicesHandler : ICommandHandler<ListDevicesCommand, Result<IList<DaemonDeviceDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListDevicesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DaemonDeviceDto>>> HandleAsync(ListDevicesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices
            .Where(d => d.UserId == command.Request.UserId)
            .OrderByDescending(d => d.LastHeartbeatAt)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<DaemonDeviceDto>)devices.Select(d => CreateDeviceHandler.MapDevice(d, null, now)).ToList());
    }
}

public class RevokeDeviceHandler : ICommandHandler<RevokeDeviceCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RevokeDeviceHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(RevokeDeviceCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure("Device not found.");
        device.Revoke();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public class HeartbeatDeviceHandler : ICommandHandler<HeartbeatDeviceCommand, Result<DaemonDeviceDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public HeartbeatDeviceHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DaemonDeviceDto>> HandleAsync(HeartbeatDeviceCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<DaemonDeviceDto>("Device not found.");
        try
        {
            device.Heartbeat(command.Request.ProbeJson, command.Request.WorkstationJson);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<DaemonDeviceDto>(ex.Message);
        }
        RecordHostSample(db, device.Id, command.Request.ProbeJson);
        await QueueReconcileAsync(db, device, ct);
        await QueueProjectAppliesAsync(db, device, ct);
        await db.SaveChangesAsync(ct);
        var stale = await db.DeviceHostSamples
            .Where(s => s.DeviceId == device.Id)
            .OrderByDescending(s => s.SampledAt)
            .Skip(60)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.DeviceHostSamples.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
        }
        return Result.Ok(CreateDeviceHandler.MapDevice(device, null, DateTime.UtcNow));
    }

    private static async Task QueueReconcileAsync(IBeaconDb db, DaemonDevice device, CancellationToken ct)
    {
        if (device.AppliedRevision >= device.DesiredRevision)
            return;
        var inflight = await db.WorkstationCommands.AnyAsync(c =>
            c.DeviceId == device.Id
            && c.Kind == WorkstationCommandKind.ReconcileDesired
            && (c.Status == WorkstationCommandStatus.Pending || c.Status == WorkstationCommandStatus.Running), ct);
        if (inflight)
            return;
        db.WorkstationCommands.Add(WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.ReconcileDesired,
            DesiredState.ReconcilePayload(device),
            requestedByUserId: device.UserId));
    }

    private static async Task QueueProjectAppliesAsync(IBeaconDb db, DaemonDevice device, CancellationToken ct)
    {
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.DeviceId == device.Id && r.AppliedConfigRevision < r.ConfigRevision)
            .ToListAsync(ct);
        if (runtimes.Count == 0)
            return;

        var pending = await db.WorkstationCommands
            .Where(c => c.DeviceId == device.Id
                && c.Kind == WorkstationCommandKind.ApplyOpencode
                && (c.Status == WorkstationCommandStatus.Pending || c.Status == WorkstationCommandStatus.Running))
            .Select(c => c.ProjectId)
            .ToListAsync(ct);
        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == device.UserId)
            .ToListAsync(ct);
        var dtos = backends.Select(ModelBackendMappers.ToDto).ToList();
        foreach (var runtime in runtimes)
        {
            if (pending.Contains(runtime.ProjectId))
                continue;
            var bindings = await db.RoleBindings.IgnoreQueryFilters()
                .Where(r => r.ProjectId == runtime.ProjectId)
                .ToListAsync(ct);
            var payload = OpencodePayload.FromBindings(bindings, dtos, runtime.ConfigRevision);
            db.WorkstationCommands.Add(WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.ApplyOpencode,
                payload,
                runtime.ProjectId,
                device.UserId));
        }
    }

    private static void RecordHostSample(IBeaconDb db, Guid deviceId, string? probeJson)
    {
        var host = DeviceLlamaSwapProxy.ParseProbe(probeJson ?? "{}")?.Host;
        if (host is null)
            return;
        db.DeviceHostSamples.Add(DeviceHostSample.Create(
            deviceId,
            (host.SampledAt ?? DateTimeOffset.UtcNow).UtcDateTime,
            host.CpuPercent,
            host.RamUsedBytes,
            host.RamTotalBytes,
            host.Gpu?.Name,
            host.Gpu?.UtilizationPercent,
            host.Gpu?.MemoryUsedBytes,
            host.Gpu?.MemoryTotalBytes));
    }
}

public class EnqueueCommandHandler : ICommandHandler<EnqueueCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public EnqueueCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(EnqueueCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Device not found.");
        if (!device.IsOnline(DateTime.UtcNow))
            return Result.Failure<WorkstationCommandDto>("Device is not connected.", ErrorKind.Conflict);

        if (command.Request.ProjectId is { } projectId)
        {
            var member = await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == command.Request.UserId, ct);
            if (!member)
                return Result.Failure<WorkstationCommandDto>("Project not found.");
        }

        string? payloadJson = command.Request.PayloadJson;
        string? localRoot = null;
        if (command.Request.Kind == WorkstationCommandKind.SaveWorkstation)
        {
            if (!DesiredState.TryStampWorkstation(payloadJson, out var stored, out var error))
                return Result.Failure<WorkstationCommandDto>(error ?? "Invalid payload.");
            device.SetDesiredWorkstation(stored);
            payloadJson = DesiredState.WithRevision(stored, device.DesiredRevision);
        }
        if (CommandSandbox.IsProjectKind(command.Request.Kind))
        {
            if (command.Request.ProjectId is not { } rootedProjectId)
                return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired);
            var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.ProjectId == rootedProjectId && r.DeviceId == device.Id, ct);
            if (runtime is null)
                return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired);
            var sanitized = CommandSandbox.SanitizeProjectPayload(payloadJson);
            if (!sanitized.Success)
                return Result.Failure<WorkstationCommandDto>(sanitized.Error ?? "Invalid payload.");
            payloadJson = sanitized.Value;
            localRoot = runtime.LocalRoot;
            if (command.Request.Kind == WorkstationCommandKind.ApplyOpencode)
                payloadJson = DesiredState.WithRevision(payloadJson ?? "{}", runtime.ConfigRevision);
        }

        var queued = WorkstationCommand.Create(
            device.Id,
            command.Request.Kind,
            payloadJson,
            command.Request.ProjectId,
            command.Request.UserId);
        db.WorkstationCommands.Add(queued);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapCommand(queued, localRoot));
    }

    internal static async Task<string?> FindLocalRootAsync(IBeaconDb db, WorkstationCommand command, CancellationToken ct)
    {
        if (!CommandSandbox.IsProjectKind(command.Kind) || command.ProjectId is not { } projectId)
            return null;
        return await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId && r.DeviceId == command.DeviceId)
            .Select(r => r.LocalRoot)
            .FirstOrDefaultAsync(ct);
    }

    internal static WorkstationCommandDto MapCommand(WorkstationCommand command, string? localRoot = null) =>
        new(command.Id, command.DeviceId, command.ProjectId, command.RequestedByUserId, command.Kind, command.Status,
            command.PayloadJson, command.ResultJson, command.Error, command.CreatedAt, command.StartedAt, command.CompletedAt, localRoot);
}

public class ClaimNextCommandHandler : ICommandHandler<ClaimNextCommandCommand, Result<WorkstationCommandDto?>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ClaimNextCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto?>> HandleAsync(ClaimNextCommandCommand command, CancellationToken ct = default)
    {
        var wait = command.Request.Wait ?? TimeSpan.Zero;
        var deadline = DateTime.UtcNow + wait;
        do
        {
            await using var db = _dbFactory.CreateDbContext();
            var next = await db.WorkstationCommands
                .Where(c => c.DeviceId == command.Request.DeviceId && c.Status == WorkstationCommandStatus.Pending)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                string? localRoot = null;
                if (CommandSandbox.IsProjectKind(next.Kind))
                {
                    localRoot = await EnqueueCommandHandler.FindLocalRootAsync(db, next, ct);
                    if (string.IsNullOrWhiteSpace(localRoot))
                    {
                        next.Fail(CommandSandbox.RuntimeRequired);
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }

                next.Claim();
                await db.SaveChangesAsync(ct);
                return Result.Ok<WorkstationCommandDto?>(EnqueueCommandHandler.MapCommand(next, localRoot));
            }

            if (DateTime.UtcNow >= deadline)
                return Result.Ok<WorkstationCommandDto?>(null);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException)
            {
                return Result.Ok<WorkstationCommandDto?>(null);
            }
        } while (!ct.IsCancellationRequested);

        return Result.Ok<WorkstationCommandDto?>(null);
    }
}

public class CompleteCommandHandler : ICommandHandler<CompleteCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CompleteCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(CompleteCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null || row.DeviceId != command.Request.DeviceId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        try
        {
            if (command.Request.Success)
            {
                await CompleteEvalRunAsync(db, row, command.Request.ResultJson, ct);
                await ApplyReviewCheckAsync(db, row, true, command.Request.ResultJson, null, ct);
                await MarkDesiredAppliedAsync(db, row, command.Request.ResultJson, ct);
                row.Succeed(command.Request.ResultJson);
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(command.Request.Error) ? "Command failed." : command.Request.Error;
                await FailEvalRunAsync(db, row, error, ct);
                await ApplyReviewCheckAsync(db, row, false, command.Request.ResultJson, error, ct);
                row.Fail(error);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<WorkstationCommandDto>(ex.Message);
        }
        await db.SaveChangesAsync(ct);
        var localRoot = await EnqueueCommandHandler.FindLocalRootAsync(db, row, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
    }

    private static async Task MarkDesiredAppliedAsync(IBeaconDb db, WorkstationCommand row, string? resultJson, CancellationToken ct)
    {
        var revision = DesiredState.ReadRevision(row.PayloadJson) ?? DesiredState.ReadRevision(resultJson);
        if (revision is not { } value)
            return;
        if (row.Kind == WorkstationCommandKind.ApplyOpencode && row.ProjectId is { } projectId)
        {
            var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.DeviceId == row.DeviceId, ct);
            runtime?.MarkConfigApplied(value);
            return;
        }
        if (row.Kind is not (WorkstationCommandKind.SaveWorkstation or WorkstationCommandKind.ReconcileDesired))
            return;
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        device?.MarkApplied(value);
    }

    private static async Task CompleteEvalRunAsync(IBeaconDb db, WorkstationCommand row, string? resultJson, CancellationToken ct)
    {
        var runId = ReadEvalRunId(row.PayloadJson) ?? ReadEvalRunId(resultJson);
        if (runId is null || string.IsNullOrWhiteSpace(resultJson))
            return;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var r = doc.RootElement;
            var promptTokens = ReadInt(r, "promptTokens");
            var completionTokens = ReadInt(r, "completionTokens");
            var turnCount = ReadInt(r, "turnCount");
            var interrupted = r.TryGetProperty("interrupted", out var interruptedElement)
                && interruptedElement.ValueKind == JsonValueKind.True;
            int? exitCode = interrupted ? null : ReadNullableInt(r, "exitCode");
            string? checkOutput = r.TryGetProperty("checkOutput", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
                ? outputElement.GetString()
                : null;
            string? transcriptRef = r.TryGetProperty("transcriptRef", out var transcriptElement) && transcriptElement.ValueKind == JsonValueKind.String
                ? transcriptElement.GetString()
                : null;
            var run = await db.EvalRuns.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == runId.Value, ct);
            if (run is null || run.CompletedAt is not null)
                return;
            run.Complete(promptTokens, completionTokens, turnCount, exitCode, transcriptRef, checkOutput);
        }
        catch (JsonException)
        {
        }
    }

    private static async Task FailEvalRunAsync(IBeaconDb db, WorkstationCommand row, string error, CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.RunEvalTurn)
            return;
        var runId = ReadEvalRunId(row.PayloadJson);
        if (runId is null)
            return;
        var run = await db.EvalRuns.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == runId.Value, ct);
        if (run is null || run.CompletedAt is not null)
            return;
        run.Complete(0, 0, 0, null, null, error);
    }

    private static async Task ApplyReviewCheckAsync(
        IBeaconDb db, WorkstationCommand row, bool commandSucceeded, string? resultJson, string? error, CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.RunReviewCheck)
            return;
        var reviewRunId = ReadGuid(row.PayloadJson, "reviewRunId");
        if (reviewRunId is null)
            return;
        var run = await db.ReviewRuns.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == reviewRunId.Value, ct);
        if (run is null || run.Status != ReviewRunStatus.Started)
            return;
        if (row.ProjectId is Guid projectId && run.ProjectId != projectId)
            return;

        if (!commandSucceeded)
        {
            run.Fail(error);
            return;
        }

        int? exitCode = null;
        string? output = null;
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                exitCode = ReadNullableInt(doc.RootElement, "exitCode");
                if (doc.RootElement.TryGetProperty("checkOutput", out var outputElement) && outputElement.ValueKind == JsonValueKind.String)
                    output = outputElement.GetString();
            }
            catch (JsonException)
            {
                exitCode = null;
            }
        }

        if (exitCode == 0)
            run.Complete(output, $"cmd:{row.Id:D}");
        else
            run.Fail(string.IsNullOrWhiteSpace(output) ? "Check did not pass." : output);
    }

    private static Guid? ReadGuid(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id))
                return id;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static int? ReadNullableInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var number))
            return number;
        return null;
    }

    private static Guid? ReadEvalRunId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("evalRunId", out var value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id))
                return id;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static int ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var number))
            return number;
        return 0;
    }
}

public class GetCommandHandler : ICommandHandler<GetCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(GetCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var localRoot = await EnqueueCommandHandler.FindLocalRootAsync(db, row, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
    }
}

public class ListCommandsHandler : ICommandHandler<ListCommandsCommand, Result<IList<WorkstationCommandDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListCommandsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<WorkstationCommandDto>>> HandleAsync(ListCommandsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<IList<WorkstationCommandDto>>("Device not found.");
        var limit = Math.Clamp(command.Request.Limit, 1, 100);
        var rows = await db.WorkstationCommands
            .Where(c => c.DeviceId == command.Request.DeviceId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        var projectIds = rows
            .Where(r => r.ProjectId is not null && CommandSandbox.IsProjectKind(r.Kind))
            .Select(r => r.ProjectId!.Value)
            .Distinct()
            .ToList();
        var roots = projectIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(r => r.DeviceId == command.Request.DeviceId && projectIds.Contains(r.ProjectId))
                .ToDictionaryAsync(r => r.ProjectId, r => r.LocalRoot, ct);
        var mapped = rows.Select(row =>
        {
            string? root = null;
            if (row.ProjectId is { } projectId && CommandSandbox.IsProjectKind(row.Kind))
                roots.TryGetValue(projectId, out root);
            return EnqueueCommandHandler.MapCommand(row, root);
        }).ToList();
        return Result.Ok((IList<WorkstationCommandDto>)mapped);
    }
}

public class AttachRuntimeHandler : ICommandHandler<AttachRuntimeCommand, Result<ProjectRuntimeDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public AttachRuntimeHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProjectRuntimeDto>> HandleAsync(AttachRuntimeCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (string.IsNullOrWhiteSpace(request.LocalRoot) || request.LocalRoot.Trim().Length > 1000)
            return Result.Failure<ProjectRuntimeDto>("LocalRoot is required (max 1000 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == request.UserId, ct);
        if (!member)
            return Result.Failure<ProjectRuntimeDto>("Project not found.");

        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != request.UserId)
            return Result.Failure<ProjectRuntimeDto>("Device not found.");

        var existing = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ProjectId == request.ProjectId && r.DeviceId == request.DeviceId, ct);
        if (existing is not null)
        {
            existing.SetLocalRoot(request.LocalRoot);
            await db.SaveChangesAsync(ct);
            return Result.Ok(MapRuntime(existing, device, DateTime.UtcNow));
        }

        var runtime = ProjectRuntime.Create(request.ProjectId, request.DeviceId, request.LocalRoot);
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapRuntime(runtime, device, DateTime.UtcNow));
    }

    internal static ProjectRuntimeDto MapRuntime(ProjectRuntime runtime, DaemonDevice device, DateTime utcNow) =>
        new(runtime.Id, runtime.ProjectId, runtime.DeviceId, device.Name, device.IsOnline(utcNow),
            runtime.LocalRoot, runtime.CreatedAt, runtime.UpdatedAt);
}

public class ListRuntimesHandler : ICommandHandler<ListRuntimesCommand, Result<IList<ProjectRuntimeDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListRuntimesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ProjectRuntimeDto>>> HandleAsync(ListRuntimesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == command.Request.ProjectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure<IList<ProjectRuntimeDto>>("Project not found.");

        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == command.Request.ProjectId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var deviceIds = runtimes.Select(r => r.DeviceId).ToList();
        var devices = await db.DaemonDevices.Where(d => deviceIds.Contains(d.Id)).ToListAsync(ct);
        var byId = devices.ToDictionary(d => d.Id);
        var now = DateTime.UtcNow;
        var dtos = new List<ProjectRuntimeDto>();
        foreach (var runtime in runtimes)
        {
            if (!byId.TryGetValue(runtime.DeviceId, out var device))
                continue;
            dtos.Add(AttachRuntimeHandler.MapRuntime(runtime, device, now));
        }
        return Result.Ok((IList<ProjectRuntimeDto>)dtos);
    }
}

public class DetachRuntimeHandler : ICommandHandler<DetachRuntimeCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DetachRuntimeHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DetachRuntimeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == command.Request.RuntimeId, ct);
        if (runtime is null)
            return Result.Failure("Runtime not found.");
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == runtime.ProjectId && m.UserId == command.Request.UserId, ct);
        if (!member)
            return Result.Failure("Runtime not found.");
        db.ProjectRuntimes.Remove(runtime);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public class GetLlamaSwapConfigHandler : ICommandHandler<GetLlamaSwapConfigCommand, Result<LlamaSwapConfigDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ILlamaSwapCatalog _catalog;

    public GetLlamaSwapConfigHandler(IBeaconDbFactory dbFactory, ILlamaSwapCatalog catalog)
    {
        _dbFactory = dbFactory;
        _catalog = catalog;
    }

    public async Task<Result<LlamaSwapConfigDto>> HandleAsync(GetLlamaSwapConfigCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<LlamaSwapConfigDto>("Device not found.");

        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == device.UserId && b.BackendType == ModelBackendType.LlamaCpp)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var specs = backends
            .Select(b => new LlamaSwapModelBinding(b.Name, b.LaunchCommand, b.ContextSize, b.Ttl, b.ExtraFlags, b.Concurrent))
            .ToList();
        return Result.Ok(new LlamaSwapConfigDto(_catalog.GenerateYaml(specs), _catalog.Port));
    }
}

public class ListHostSamplesHandler : ICommandHandler<ListHostSamplesCommand, Result<IList<DeviceHostSampleDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListHostSamplesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DeviceHostSampleDto>>> HandleAsync(ListHostSamplesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var devices = db.DaemonDevices.Where(d => d.UserId == command.Request.UserId && d.RevokedAt == null);
        if (command.Request.DeviceId is { } deviceId)
            devices = devices.Where(d => d.Id == deviceId);
        var deviceIds = await devices.Select(d => d.Id).ToListAsync(ct);
        if (deviceIds.Count == 0)
            return Result.Ok((IList<DeviceHostSampleDto>)[]);

        var samples = await db.DeviceHostSamples
            .Where(s => deviceIds.Contains(s.DeviceId))
            .OrderBy(s => s.SampledAt)
            .ToListAsync(ct);
        return Result.Ok((IList<DeviceHostSampleDto>)samples.Select(s => new DeviceHostSampleDto(
            s.Id, s.DeviceId, s.SampledAt, s.CpuPercent, s.RamUsedBytes, s.RamTotalBytes,
            s.GpuName, s.GpuUtilizationPercent, s.GpuMemoryUsedBytes, s.GpuMemoryTotalBytes)).ToList());
    }
}
