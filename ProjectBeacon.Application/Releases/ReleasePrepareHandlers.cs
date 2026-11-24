namespace ProjectBeacon.Application.Releases;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Domain.Common;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Request to queue ReleasePrepare on the project's pinned release workstation.</summary>
public sealed record PrepareReleaseCommand(
    Guid ProjectId,
    Guid ReleaseId,
    int ExpectedReleaseRevision,
    ActorContext Actor);

/// <summary>Request to queue VerifyLanding for the exact prepared Candidate.</summary>
public sealed record VerifyReleaseLandingCommand(
    Guid ProjectId,
    Guid ReleaseId,
    int ExpectedPreparationRevision,
    ActorContext Actor);

public sealed record ReleasePrepareOperationDto(
    ReleaseDto Release,
    WorkstationCommandDto Command);

internal static class ReleasePreparePayload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<(string? Json, int MilestoneCount, string? Error)> BuildSnapshotAsync(
        IBeaconDb db,
        Release release,
        Project project,
        ProjectVersionPolicy policy,
        CancellationToken ct)
    {
        var milestones = await db.Milestones.AsNoTracking()
            .Where(milestone => milestone.ProjectId == release.ProjectId && milestone.ReleaseId == release.Id)
            .Include(milestone => milestone.Tasks)
            .OrderBy(milestone => milestone.Order)
            .ThenBy(milestone => milestone.Id)
            .ToListAsync(ct);

        var warnings = new List<string>();
        var milestoneSnapshots = new List<object>();
        foreach (var milestone in milestones)
        {
            var tasks = milestone.Tasks
                .OrderBy(task => task.CreatedAt)
                .ThenBy(task => task.Id)
                .ToArray();
            var taskSnapshots = new List<object>();
            foreach (var task in tasks)
            {
                if (task.VersionImpact is null)
                    warnings.Add($"Task {task.Id:D} has no version-impact declaration.");
                else if (!string.Equals(task.VersionImpactReviewStatus, "Confirmed", StringComparison.Ordinal))
                    warnings.Add(string.Equals(task.VersionImpactReviewStatus, "Challenged", StringComparison.Ordinal)
                        ? $"Task {task.Id:D} has a challenged version-impact declaration."
                        : $"Task {task.Id:D} version impact has not been confirmed by Reviewer.");

                taskSnapshots.Add(new
                {
                    taskId = task.Id,
                    title = task.Title,
                    status = task.Status.ToString(),
                    priority = task.Priority.ToString(),
                    type = task.Type.ToString(),
                    subStage = task.SubStage.ToString(),
                    stateRevision = task.StateRevision,
                    contentRevision = task.ContentRevision,
                    versionImpact = task.VersionImpact,
                    versionImpactRationale = task.VersionImpactRationale,
                    versionImpactRevision = task.VersionImpactRevision,
                    versionImpactReviewStatus = task.VersionImpactReviewStatus,
                    versionImpactReviewNote = task.VersionImpactReviewNote,
                    versionImpactReviewedAt = task.VersionImpactReviewedAt,
                    completedAt = task.CompletedAt
                });
            }

            milestoneSnapshots.Add(new
            {
                milestoneId = milestone.Id,
                milestone.Name,
                milestone.Description,
                milestone.Order,
                milestone.Revision,
                milestone.ClosedAt,
                tasks = taskSnapshots
            });
        }

        var taskCount = milestones.Sum(milestone => milestone.Tasks.Count);
        var snapshot = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTime.UtcNow,
            projectId = project.Id,
            projectName = project.Name,
            officialVersionBeforeRelease = project.OfficialVersion,
            versionPolicyRevision = project.VersionPolicyRevision,
            versionPolicy = new
            {
                policy.Scheme,
                policy.ComponentCount,
                policy.CalendarFormat,
                policy.TagPattern,
                policy.AllowPrerelease,
                policy.Sources
            },
            release = new
            {
                releaseId = release.Id,
                release.Name,
                release.Description,
                targetVersion = release.TargetVersion,
                targetBranch = release.TargetBranch,
                status = release.Status.ToString(),
                releaseRevision = release.Revision,
                releaseOrder = release.Order
            },
            milestoneCount = milestoneSnapshots.Count,
            taskCount,
            milestones = milestoneSnapshots,
            warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (json.Length > 1_000_000)
            return (null, milestones.Count, "Release snapshot exceeds the 1 MB immutable evidence limit.");
        return (json, milestones.Count, null);
    }

    public static bool TryReadObject(string? json, out JsonObject value)
    {
        value = new JsonObject();
        try
        {
            value = JsonNode.Parse(json ?? string.Empty) as JsonObject ?? new JsonObject();
            return value.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Text(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<string>(out var text) ? text ?? string.Empty : string.Empty;

    public static int Integer(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<int>(out var number) ? number : 0;

    public static Guid GuidValue(JsonObject value, string name) =>
        Guid.TryParse(Text(value, name), out var id) ? id : Guid.Empty;

    public static bool IsGitSha(string value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    public static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static WorkstationCommandDto ToCommandDto(WorkstationCommand command, string localRoot) =>
        new(command.Id, command.DeviceId, command.ProjectId, command.RequestedByUserId,
            command.Kind, command.Status, command.PayloadJson, command.ResultJson, command.Error,
            command.CreatedAt, command.StartedAt, command.CompletedAt, localRoot,
            command.Version, command.Attempts + 1);
}

public sealed class PrepareReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public PrepareReleaseHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<ReleasePrepareOperationDto>> HandleAsync(
        PrepareReleaseCommand command, CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.ReleaseId == Guid.Empty
            || command.ExpectedReleaseRevision < 1)
            return Result.Failure<ReleasePrepareOperationDto>(
                "Project, Release and positive release revision are required.", ErrorKind.Validation);
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ReleasePrepareOperationDto>(
                "Only a human project Owner/Admin can prepare a release.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, command.ProjectId, ct))
                return Result.Failure<ReleasePrepareOperationDto>("Project not found.", ErrorKind.NotFound);

            var permission = await ReleaseAuthorization.RequireAdminAsync(
                db, _authorization, command.ProjectId, command.Actor, ct);
            if (!permission.Success)
                return Result.Forbidden<ReleasePrepareOperationDto>(
                    "Only a project or organization Owner/Admin can prepare a release.");

            var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, command.ProjectId, ct);
            if (!enabled.Success)
                return Result.Failure<ReleasePrepareOperationDto>(
                    enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

            var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
            var release = await db.Releases.FirstOrDefaultAsync(
                item => item.Id == command.ReleaseId && item.ProjectId == command.ProjectId, ct);
            if (project is null || release is null)
                return Result.Failure<ReleasePrepareOperationDto>("Release not found.", ErrorKind.NotFound);
            if (release.Status != ReleaseStatus.InProgress)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Set the Release to InProgress before preparing a Candidate.", ErrorKind.Conflict);
            if (release.Revision != command.ExpectedReleaseRevision)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release changed since it was loaded. Refresh and retry.", ErrorKind.Conflict);
            if (!ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var policy, out var policyError))
                return Result.Failure<ReleasePrepareOperationDto>(
                    policyError ?? "Project version policy is invalid.", ErrorKind.Conflict);
            if (!policy.ReleaseManagementEnabled)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release Management is disabled for this project.", ErrorKind.Conflict);
            if (policy.Sources.Count is < 1 or > 16)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "ReleasePrepare requires 1-16 configured version-file projections.", ErrorKind.Validation);
            if (string.IsNullOrWhiteSpace(release.TargetVersion)
                || !ProjectVersionPolicyCatalog.TryNormalizeVersion(
                    policy, release.TargetVersion, out var version, out var versionError))
                return Result.Failure<ReleasePrepareOperationDto>(
                    versionError ?? "Set a valid target version on the Release before preparing.", ErrorKind.Validation);
            if (!ProjectVersionPolicyCatalog.IsSafeBranchName(release.TargetBranch))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release target branch is not a safe Git branch name.", ErrorKind.Validation);
            if (await ProjectRuntimeMutationGate.HasActiveRepositoryOperationAsync(db, command.ProjectId, ct))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "ReleasePrepare cannot start while another repository command or agent session is active.",
                    ErrorKind.Conflict);

            var selected = await ResolvePinnedWorkstationAsync(
                db, release, command.Actor.UserId.Value, _timeProvider.GetUtcNow().UtcDateTime, ct);
            if (!selected.Success)
                return Result.Failure<ReleasePrepareOperationDto>(selected.Error!, selected.Kind);

            string payloadJson;
            Guid candidateId;
            if (release.PreparationStatus == ReleasePreparationStatus.NotStarted)
            {
                var snapshot = await ReleasePreparePayload.BuildSnapshotAsync(
                    db, release, project, policy, ct);
                if (snapshot.Error is not null)
                    return Result.Failure<ReleasePrepareOperationDto>(snapshot.Error, ErrorKind.Validation);
                candidateId = Guid.NewGuid();
                var payload = new JsonObject
                {
                    ["action"] = "Prepare",
                    ["projectId"] = command.ProjectId.ToString("D"),
                    ["releaseId"] = command.ReleaseId.ToString("D"),
                    ["candidateId"] = candidateId.ToString("D"),
                    ["targetBranch"] = release.TargetBranch,
                    ["version"] = version,
                    ["versionPolicyJson"] = project.VersionPolicyJson,
                    ["versionPolicyRevision"] = project.VersionPolicyRevision,
                    ["releaseRevision"] = release.Revision,
                    ["snapshotJson"] = snapshot.Json
                };
                payloadJson = payload.ToJsonString();
            }
            else if (release.PreparationStatus == ReleasePreparationStatus.Failed
                && release.PreparationPayloadJson is { Length: > 0 } existingPayload
                && release.CandidateId is { } existingCandidate)
            {
                if (!ReleasePreparePayload.TryReadObject(existingPayload, out var existing)
                    || ReleasePreparePayload.Integer(existing, "versionPolicyRevision") != project.VersionPolicyRevision
                    || ReleasePreparePayload.Integer(existing, "releaseRevision") != release.Revision
                    || !string.Equals(ReleasePreparePayload.Text(existing, "version"), version, StringComparison.Ordinal))
                    return Result.Failure<ReleasePrepareOperationDto>(
                        "The original Candidate snapshot no longer matches the current release/version policy. Create a new Release instead of retargeting this preparation.",
                        ErrorKind.Conflict);
                payloadJson = existingPayload;
                candidateId = existingCandidate;
            }
            else
            {
                return Result.Failure<ReleasePrepareOperationDto>(
                    "ReleasePrepare is already queued or has advanced. Use VerifyLanding or wait for the current command.",
                    ErrorKind.Conflict);
            }

            var sanitized = CommandSandbox.SanitizeProjectPayload(payloadJson, WorkstationCommandKind.ReleasePrepare);
            if (!sanitized.Success)
                return Result.Failure<ReleasePrepareOperationDto>(
                    sanitized.Error ?? "ReleasePrepare payload failed sandbox validation.", ErrorKind.Validation);

            var commandRow = WorkstationCommand.Create(
                selected.Device!.Id, WorkstationCommandKind.ReleasePrepare, sanitized.Value,
                command.ProjectId, command.Actor.UserId);
            try
            {
                release.BeginPreparation(selected.Device!.Id, candidateId, commandRow.Id, sanitized.Value!);
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure<ReleasePrepareOperationDto>(exception.Message, ErrorKind.Conflict);
            }

            db.WorkstationCommands.Add(commandRow);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor,
                "project.release.prepare_queued",
                "Release",
                release.Id.ToString("D"),
                project.OrgId,
                command.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    candidateId,
                    workstationId = selected.Device!.Id,
                    targetVersion = release.TargetVersion,
                    targetBranch = release.TargetBranch,
                    preparationRevision = release.PreparationRevision
                })));

            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release or workstation state changed concurrently. Refresh and retry.", ErrorKind.Conflict);
            }

            var milestoneCount = await db.Milestones.CountAsync(
                milestone => milestone.ProjectId == command.ProjectId && milestone.ReleaseId == release.Id, ct);
            return Result.Ok(new ReleasePrepareOperationDto(
                ReleaseMapping.ToDto(release, milestoneCount),
                ReleasePreparePayload.ToCommandDto(commandRow, selected.Runtime!.LocalRoot)));
        });
    }

    private static async Task<(bool Success, DaemonDevice? Device, ProjectRuntime? Runtime, ErrorKind Kind, string? Error)> ResolvePinnedWorkstationAsync(
        IBeaconDb db,
        Release release,
        Guid userId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (release.PreparationDeviceId is { } pinnedDeviceId)
        {
            var pinnedRuntime = await db.ProjectRuntimes.FirstOrDefaultAsync(
                runtime => runtime.ProjectId == release.ProjectId && runtime.DeviceId == pinnedDeviceId, ct);
            var pinnedDevice = await db.DaemonDevices.FirstOrDefaultAsync(
                device => device.Id == pinnedDeviceId && device.UserId == userId, ct);
            if (pinnedRuntime is null || pinnedDevice is null || !pinnedDevice.IsOnline(nowUtc)
                || !WorkstationCapabilities.Supports(pinnedDevice.ProbeJson, WorkstationCapabilities.ReleasePrepareV1))
                return (false, null, null, ErrorKind.Conflict,
                    "The Release's pinned workstation is offline, detached or no longer supports ReleasePrepare. Automatic workstation switching is forbidden.");
            return (true, pinnedDevice, pinnedRuntime, ErrorKind.Validation, null);
        }

        var runtimes = await db.ProjectRuntimes
            .Where(runtime => runtime.ProjectId == release.ProjectId)
            .ToListAsync(ct);
        var deviceIds = runtimes.Select(runtime => runtime.DeviceId).Distinct().ToArray();
        if (deviceIds.Length == 0)
            return (false, null, null, ErrorKind.Conflict, "Attach a project runtime to a ReleasePrepare-capable workstation first.");

        var devices = await db.DaemonDevices
            .Where(device => deviceIds.Contains(device.Id) && device.UserId == userId && device.RevokedAt == null)
            .ToListAsync(ct);
        var selected = runtimes
            .Join(devices, runtime => runtime.DeviceId, device => device.Id, (runtime, device) => (Runtime: runtime, Device: device))
            .Where(item => item.Device.IsOnline(nowUtc)
                && WorkstationCapabilities.Supports(item.Device.ProbeJson, WorkstationCapabilities.ReleasePrepareV1))
            .OrderByDescending(item => item.Device.LastHeartbeatAt)
            .ThenBy(item => item.Device.Id)
            .FirstOrDefault();
        return selected.Device is null
            ? (false, null, null, ErrorKind.Conflict, "No online workstation owned by the requesting user advertises ReleasePrepare support.")
            : (true, selected.Device, selected.Runtime, ErrorKind.Validation, null);
    }
}

public sealed class VerifyReleaseLandingHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public VerifyReleaseLandingHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<ReleasePrepareOperationDto>> HandleAsync(
        VerifyReleaseLandingCommand command, CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.ReleaseId == Guid.Empty
            || command.ExpectedPreparationRevision < 1)
            return Result.Failure<ReleasePrepareOperationDto>(
                "Project, Release and positive preparation revision are required.", ErrorKind.Validation);
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ReleasePrepareOperationDto>(
                "Only a human project Owner/Admin can verify a Release Candidate.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, command.ProjectId, ct))
                return Result.Failure<ReleasePrepareOperationDto>("Project not found.", ErrorKind.NotFound);

            var permission = await ReleaseAuthorization.RequireAdminAsync(
                db, _authorization, command.ProjectId, command.Actor, ct);
            if (!permission.Success)
                return Result.Forbidden<ReleasePrepareOperationDto>(
                    "Only a project or organization Owner/Admin can verify a release.");

            var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, command.ProjectId, ct);
            if (!enabled.Success)
                return Result.Failure<ReleasePrepareOperationDto>(
                    enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

            var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
            var release = await db.Releases.FirstOrDefaultAsync(
                item => item.Id == command.ReleaseId && item.ProjectId == command.ProjectId, ct);
            if (project is null || release is null)
                return Result.Failure<ReleasePrepareOperationDto>("Release not found.", ErrorKind.NotFound);
            if (release.PreparationRevision != command.ExpectedPreparationRevision)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release preparation state changed. Refresh before verifying.", ErrorKind.Conflict);
            if (release.PreparationStatus is not (ReleasePreparationStatus.Prepared or ReleasePreparationStatus.VerifyFailed))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release must be Prepared or VerifyFailed before verification.", ErrorKind.Conflict);
            if (release.PreparationDeviceId is not { } pinnedDeviceId
                || release.PreparationPayloadJson is not { Length: > 0 } preparePayloadJson
                || release.PreparationResultJson is not { Length: > 0 } prepareResultJson)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "The pinned workstation or validated Prepare result is missing.", ErrorKind.Conflict);

            if (!ReleasePreparePayload.TryReadObject(preparePayloadJson, out var preparePayload)
                || !ReleasePreparePayload.TryReadObject(prepareResultJson, out var prepareResult))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Stored ReleasePrepare payload or result is invalid. The Candidate cannot be verified.", ErrorKind.Conflict);

            if (ReleasePreparePayload.Integer(preparePayload, "versionPolicyRevision") != project.VersionPolicyRevision
                || ReleasePreparePayload.Integer(preparePayload, "releaseRevision") != release.Revision)
                return Result.Failure<ReleasePrepareOperationDto>(
                    "The project version policy or Release changed after Prepare. The immutable Candidate must be recreated as a new Release.",
                    ErrorKind.Conflict);

            var expectedCandidateId = release.CandidateId;
            if (expectedCandidateId is null
                || expectedCandidateId != ReleasePreparePayload.GuidValue(preparePayload, "candidateId"))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Stored Candidate identity does not match the Release.", ErrorKind.Conflict);

            var runtime = await db.ProjectRuntimes.FirstOrDefaultAsync(
                item => item.ProjectId == command.ProjectId && item.DeviceId == pinnedDeviceId, ct);
            var device = await db.DaemonDevices.FirstOrDefaultAsync(
                item => item.Id == pinnedDeviceId && item.UserId == command.Actor.UserId, ct);
            if (runtime is null || device is null || !device.IsOnline(_timeProvider.GetUtcNow().UtcDateTime)
                || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.ReleasePrepareV1))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "The workstation pinned by Prepare is unavailable. VerifyLanding will not switch stations.",
                    ErrorKind.Conflict);
            if (await ProjectRuntimeMutationGate.HasActiveRepositoryOperationAsync(db, command.ProjectId, ct))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "VerifyLanding cannot start while another repository command or agent session is active.",
                    ErrorKind.Conflict);

            var preparedCommit = ReleasePreparePayload.Text(prepareResult, "preparedCommitSha");
            var manifestSha = ReleasePreparePayload.Text(prepareResult, "manifestSha256");
            if (!ReleasePreparePayload.IsGitSha(preparedCommit) || !ReleasePreparePayload.IsSha256(manifestSha))
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Validated Prepare result has an invalid commit SHA or manifest hash.", ErrorKind.Conflict);

            var verifyPayload = (JsonObject)preparePayload.DeepClone();
            verifyPayload["action"] = "VerifyLanding";
            verifyPayload["preparedCommitSha"] = preparedCommit;
            verifyPayload["manifestSha256"] = manifestSha;
            var sanitized = CommandSandbox.SanitizeProjectPayload(
                verifyPayload.ToJsonString(), WorkstationCommandKind.ReleasePrepare);
            if (!sanitized.Success)
                return Result.Failure<ReleasePrepareOperationDto>(
                    sanitized.Error ?? "VerifyLanding payload failed sandbox validation.", ErrorKind.Validation);

            var commandRow = WorkstationCommand.Create(
                device.Id, WorkstationCommandKind.ReleasePrepare, sanitized.Value,
                command.ProjectId, command.Actor.UserId);
            try
            {
                release.BeginVerification(device.Id, commandRow.Id);
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure<ReleasePrepareOperationDto>(exception.Message, ErrorKind.Conflict);
            }
            db.WorkstationCommands.Add(commandRow);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor,
                "project.release.verify_queued",
                "Release",
                release.Id.ToString("D"),
                project.OrgId,
                command.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    candidateId = release.CandidateId,
                    workstationId = pinnedDeviceId,
                    preparedCommitSha = preparedCommit,
                    preparationRevision = release.PreparationRevision
                })));
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ReleasePrepareOperationDto>(
                    "Release preparation changed concurrently. Refresh and retry.", ErrorKind.Conflict);
            }

            var milestoneCount = await db.Milestones.CountAsync(
                milestone => milestone.ProjectId == command.ProjectId && milestone.ReleaseId == release.Id, ct);
            return Result.Ok(new ReleasePrepareOperationDto(
                ReleaseMapping.ToDto(release, milestoneCount),
                ReleasePreparePayload.ToCommandDto(commandRow, runtime.LocalRoot)));
        });
    }
}
