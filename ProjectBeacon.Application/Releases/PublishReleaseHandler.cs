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
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Explicit Owner/Admin confirmation to create the immutable release tag and verify deployment.</summary>
public sealed record PublishReleaseCommand(
    Guid ProjectId,
    Guid ReleaseId,
    int ExpectedReleaseRevision,
    int ExpectedPublicationRevision,
    ActorContext Actor);

public sealed record PublishReleaseOperationDto(
    ReleaseDto Release,
    WorkstationCommandDto Command);

public sealed class PublishReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public PublishReleaseHandler(
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

    public async Task<Result<PublishReleaseOperationDto>> HandleAsync(
        PublishReleaseCommand command, CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.ReleaseId == Guid.Empty
            || command.ExpectedReleaseRevision < 1 || command.ExpectedPublicationRevision < 1)
            return Result.Failure<PublishReleaseOperationDto>(
                "Project, Release and positive release/publication revisions are required.", ErrorKind.Validation);
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<PublishReleaseOperationDto>(
                "Publishing a release requires explicit confirmation by a human project Owner/Admin.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, command.ProjectId, ct))
                return Result.Failure<PublishReleaseOperationDto>("Project not found.", ErrorKind.NotFound);

            var permission = await ReleaseAuthorization.RequireAdminAsync(
                db, _authorization, command.ProjectId, command.Actor, ct);
            if (!permission.Success)
                return Result.Forbidden<PublishReleaseOperationDto>(
                    "Only a project or organization Owner/Admin can publish a release.");

            var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, command.ProjectId, ct);
            if (!enabled.Success)
                return Result.Failure<PublishReleaseOperationDto>(
                    enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

            var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
            var release = await db.Releases.FirstOrDefaultAsync(
                item => item.Id == command.ReleaseId && item.ProjectId == command.ProjectId, ct);
            if (project is null || release is null)
                return Result.Failure<PublishReleaseOperationDto>("Release not found.", ErrorKind.NotFound);
            if (release.Revision != command.ExpectedReleaseRevision
                || release.PublicationRevision != command.ExpectedPublicationRevision)
                return Result.Failure<PublishReleaseOperationDto>(
                    "Release or publication state changed. Refresh before confirming publication.", ErrorKind.Conflict);
            if (release.Status != ReleaseStatus.Candidate || release.PreparationStatus != ReleasePreparationStatus.Candidate)
                return Result.Failure<PublishReleaseOperationDto>(
                    "Only a verified Candidate can be published.", ErrorKind.Conflict);

            var candidate = await db.ReleaseCandidateSnapshots.AsNoTracking().FirstOrDefaultAsync(
                item => item.ProjectId == command.ProjectId && item.ReleaseId == command.ReleaseId
                    && item.CandidateId == release.CandidateId, ct);
            if (candidate is null
                || release.PreparationDeviceId is not { } pinnedDeviceId
                || candidate.PreparedByDeviceId != pinnedDeviceId)
                return Result.Failure<PublishReleaseOperationDto>(
                    "Immutable Candidate snapshot or its pinned workstation is missing.", ErrorKind.Conflict);

            if (!ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var policy, out var policyError))
                return Result.Failure<PublishReleaseOperationDto>(
                    policyError ?? "Project version policy is invalid.", ErrorKind.Conflict);
            if (!policy.ReleaseManagementEnabled)
                return Result.Failure<PublishReleaseOperationDto>(
                    "Release Management is disabled for this project.", ErrorKind.Conflict);
            if (candidate.VersionPolicyRevision != project.VersionPolicyRevision
                || !string.Equals(project.OfficialVersion, candidate.Version, StringComparison.Ordinal))
                return Result.Failure<PublishReleaseOperationDto>(
                    "The current version policy or official version no longer matches the immutable Candidate. Create a new Release.", ErrorKind.Conflict);
            if (!ProjectVersionPolicyCatalog.TryValidateDeploymentVersionEndpoint(
                policy.DeploymentVersionEndpoint, out var endpointError)
                || string.IsNullOrWhiteSpace(policy.DeploymentVersionEndpoint))
                return Result.Failure<PublishReleaseOperationDto>(
                    endpointError ?? "Configure the production deployment version endpoint in Project Settings before publishing.", ErrorKind.Validation);
            if (!ProjectVersionPolicyCatalog.IsSafeGitTagName(candidate.TagName)
                || !ProjectVersionPolicyCatalog.IsSafeBranchName(candidate.TargetBranch)
                || candidate.CandidateSha.Length is not (40 or 64)
                || !candidate.CandidateSha.All(Uri.IsHexDigit)
                || candidate.CandidateTreeSha.Length is not (40 or 64)
                || !candidate.CandidateTreeSha.All(Uri.IsHexDigit))
                return Result.Failure<PublishReleaseOperationDto>(
                    "The immutable Candidate contains invalid tag or Git SHA evidence.", ErrorKind.Conflict);
            if (await ProjectRuntimeMutationGate.HasActiveRepositoryOperationAsync(db, command.ProjectId, ct))
                return Result.Failure<PublishReleaseOperationDto>(
                    "Publication cannot start while an agent session or repository operation is active.", ErrorKind.Conflict);

            var runtime = await db.ProjectRuntimes.FirstOrDefaultAsync(
                item => item.ProjectId == command.ProjectId && item.DeviceId == pinnedDeviceId, ct);
            var device = await db.DaemonDevices.FirstOrDefaultAsync(
                item => item.Id == pinnedDeviceId && item.UserId == command.Actor.UserId, ct);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (runtime is null || string.IsNullOrWhiteSpace(runtime.LocalRoot)
                || device is null || !device.IsOnline(now)
                || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.ReleasePrepareV1)
                || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.ReleasePublishV1))
                return Result.Failure<PublishReleaseOperationDto>(
                    "The workstation pinned by ReleasePrepare is offline or does not advertise release publication support. Automatic workstation switching is forbidden.",
                    ErrorKind.Conflict);

            var payload = new JsonObject
            {
                ["projectId"] = command.ProjectId.ToString("D"),
                ["releaseId"] = command.ReleaseId.ToString("D"),
                ["candidateId"] = candidate.CandidateId.ToString("D"),
                ["version"] = candidate.Version,
                ["targetBranch"] = candidate.TargetBranch,
                ["tagName"] = candidate.TagName,
                ["candidateSha"] = candidate.CandidateSha,
                ["candidateTreeSha"] = candidate.CandidateTreeSha,
                ["deploymentVersionEndpoint"] = policy.DeploymentVersionEndpoint,
                ["publicationRevision"] = release.PublicationRevision,
                ["releaseRevision"] = release.Revision
            };
            var sanitized = CommandSandbox.SanitizeProjectPayload(
                payload.ToJsonString(), WorkstationCommandKind.PublishRelease);
            if (!sanitized.Success)
                return Result.Failure<PublishReleaseOperationDto>(
                    sanitized.Error ?? "PublishRelease payload failed sandbox validation.", ErrorKind.Validation);

            var row = WorkstationCommand.Create(
                device.Id, WorkstationCommandKind.PublishRelease, sanitized.Value!,
                command.ProjectId, command.Actor.UserId);
            try
            {
                // CandidateSnapshot is persisted as a separate immutable evidence row, not
                // an EF navigation. Reattach the exact row loaded above before invoking
                // domain transitions that validate Candidate identity.
                release.AttachCandidateSnapshot(candidate);
                release.BeginPublication(device.Id, row.Id, sanitized.Value!);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Result.Failure<PublishReleaseOperationDto>(exception.Message, ErrorKind.Conflict);
            }

            db.WorkstationCommands.Add(row);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor,
                "project.release.publish_queued",
                "Release",
                release.Id.ToString("D"),
                project.OrgId,
                command.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    candidateId = candidate.CandidateId,
                    tagName = candidate.TagName,
                    candidateSha = candidate.CandidateSha,
                    workstationId = device.Id,
                    publicationRevision = release.PublicationRevision,
                    deploymentEndpointHost = new Uri(policy.DeploymentVersionEndpoint!).Host
                })));

            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<PublishReleaseOperationDto>(
                    "Release or workstation state changed concurrently. Refresh and retry.", ErrorKind.Conflict);
            }

            var milestoneCount = await db.Milestones.CountAsync(
                item => item.ProjectId == command.ProjectId && item.ReleaseId == release.Id, ct);
            return Result.Ok(new PublishReleaseOperationDto(
                ReleaseMapping.ToDto(release, milestoneCount),
                ReleasePreparePayload.ToCommandDto(row, runtime.LocalRoot)));
        });
    }
}
