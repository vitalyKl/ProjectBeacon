namespace ProjectBeacon.Application.Proposals;

using System.Text.Json;
using Application.Audit;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Content;
using Domain.Entities.Devices;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Validates and queues a documentation-only RepoPatch for the workstation.
/// The caller owns the surrounding transaction so proposal state, command and audit
/// are committed together.
/// </summary>
internal static class RepoPatchProposalWorkflow
{
    public static async Task<Result<WorkstationCommand>> QueueAsync(
        IBeaconDb db,
        Proposal proposal,
        Guid? reviewerUserId,
        ActorContext auditActor,
        IAuditWriter auditWriter,
        DateTime queuedAtUtc,
        bool requireProfileAuto,
        CancellationToken ct)
    {
        if (proposal.TargetKind != ContentTargetKind.RepoPatch
            || proposal.Operation != ProposalOperation.Update
            || proposal.TargetId != proposal.ProjectId)
            return Result.Failure<WorkstationCommand>("The proposal is not a supported project RepoPatch.");

        if (proposal.AuthorKind != ProposalAuthorKind.Agent
            || proposal.AuthorTokenId is not { } authorTokenId
            || proposal.SubtaskId is not { } subtaskId
            || proposal.TaskId is not { } taskId
            || proposal.SessionId is null)
            return Result.Forbidden<WorkstationCommand>("RepoPatch must be authored by a task-bound docs-writer Actor.");

        var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
            item.Id == subtaskId
            && item.TaskId == taskId
            && item.ProjectId == proposal.ProjectId, ct);
        if (subtask is null || !string.Equals(subtask.ProfileKey, "docs-writer", StringComparison.OrdinalIgnoreCase))
            return Result.Forbidden<WorkstationCommand>("Only the built-in docs-writer profile can apply RepoPatch.");

        var profile = BuiltInAgentProfiles.Resolve(subtask.ProfileKey, subtask.ProfileSnapshotJson);
        if (profile is null
            || !string.Equals(profile.Key, "docs-writer", StringComparison.OrdinalIgnoreCase)
            || profile.PathPolicy != AgentProfilePathPolicy.Defaults
            || profile.DefaultPaths.Count == 0)
            return Result.Forbidden<WorkstationCommand>("The docs-writer profile does not have a safe documentation path policy.");

        if (!Enum.TryParse<AgentProfileApplyPolicy>(profile.ApplyPolicy, true, out var applyPolicy)
            || !Enum.IsDefined(applyPolicy))
            return Result.Forbidden<WorkstationCommand>("The docs-writer apply policy is invalid.");
        if (requireProfileAuto && applyPolicy != AgentProfileApplyPolicy.Auto)
            return Result.Forbidden<WorkstationCommand>("Automatic application is not enabled in this docs-writer profile.");

        var selectedPaths = subtask.AllowedPaths.Count == 0
            ? profile.DefaultPaths
            : subtask.AllowedPaths;
        if (selectedPaths.Count is < 1 or > 10
            || selectedPaths.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, profile.DefaultPaths)))
            return Result.Forbidden<WorkstationCommand>("The docs-writer subtask contains a path outside its documentation-only grant.");

        if (!RepoPatchValidation.TryValidate(
                proposal.PayloadJson, selectedPaths, profile.DefaultPaths,
                out var validated, out var validationError)
            || validated is null)
            return Result.Failure<WorkstationCommand>(
                validationError ?? "The documentation patch failed validation.", ErrorKind.Validation);

        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == proposal.ProjectId, ct);
        if (project is null)
            return Result.Failure<WorkstationCommand>("Project not found.");

        var authorToken = await db.ApiTokens.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(token => token.Id == authorTokenId
                && token.ProjectId == proposal.ProjectId
                && token.ClientKind == ApiTokenClientKind.Harness
                && token.DeviceId != null, ct);
        if (authorToken?.DeviceId is not { } deviceId)
            return Result.Failure<WorkstationCommand>("The author workstation binding is no longer available.", ErrorKind.Conflict);

        var device = await db.DaemonDevices.FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        if (device is null || device.IsRevoked || !device.IsOnline(queuedAtUtc))
            return Result.Failure<WorkstationCommand>("The author workstation is offline or revoked.", ErrorKind.Conflict);

        var runtime = await ProjectRuntimeResolver.ResolveAsync(db, proposal.ProjectId, deviceId, ct);
        if (!runtime.Success)
            return Result.Failure<WorkstationCommand>(
                runtime.Error ?? CommandSandbox.RuntimeRequired, ErrorKind.Conflict);

        var payload = JsonSerializer.Serialize(new
        {
            projectId = proposal.ProjectId,
            proposalId = proposal.Id,
            patch = validated.Patch,
            files = validated.Files.Select(file => new
            {
                gitPath = file.GitPath,
                projectPath = file.ProjectPath,
                baseHash = file.BaseHash,
                resultHash = file.ResultHash
            }),
            allowedPaths = selectedPaths
        });

        var command = WorkstationCommand.Create(
            deviceId,
            WorkstationCommandKind.ApplyProposalPatch,
            payload,
            proposal.ProjectId,
            reviewerUserId);
        proposal.StartApply(command.Id, reviewerUserId, queuedAtUtc);
        db.WorkstationCommands.Add(command);

        auditWriter.Stage(db, AuditEventFactory.ForActor(
            auditActor,
            "proposal.patch.apply_queued",
            "Proposal",
            proposal.Id.ToString("D"),
            project.OrgId,
            proposal.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = ProposalStatus.Applying.ToString() })));

        return Result.Ok(command);
    }
}
