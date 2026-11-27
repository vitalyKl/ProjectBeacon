namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Application.Audit;
using Application.Authorization;
using Domain.Entities.Content;
using Domain.Entities.Audit;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Application.Persistence;
using Application.Proposals;
using Application.Projects;
using Application.Releases;
using Application.Tasks;
using Microsoft.EntityFrameworkCore;

internal interface ICommandCompletion
{
    Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct);
}

internal static class CommandCompletions
{
    private static readonly ICommandCompletion[] Effects =
    [
        new RepoPatchCompletion(),
        new EvalRunCompletion(),
        new ReviewCheckCompletion(),
        new GitLandCompletion(),
        new ReleasePrepareCompletion(),
        new ReleasePublishCompletion(),
        new DesiredStateCompletion(),
        new ChatSessionCompletion(),
        new PipelineSessionCompletion()
    ];

    public static async Task ApplyAsync(
        IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        foreach (var effect in Effects)
            await effect.ApplyAsync(db, row, success, resultJson, error, ct);
    }

    /// <summary>
    /// A completion belongs to the attempt the row is currently on: the claim generation is
    /// <c>Attempts + 1</c>. A missing generation (legacy client) is accepted only on a row that
    /// was never re-queued, so a late completion from an older attempt can never finish a
    /// newer attempt of the same command.
    /// </summary>
    public static bool AcceptsGeneration(WorkstationCommand row, int? generation) =>
        generation is null ? row.Attempts == 0 : generation == row.Attempts + 1;
}

internal sealed class RepoPatchCompletion : ICommandCompletion
{
    public async Task ApplyAsync(
        IBeaconDb db,
        WorkstationCommand row,
        bool success,
        string? resultJson,
        string? error,
        CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.ApplyProposalPatch || row.ProjectId is not { } projectId)
            return;

        Guid? proposalId = null;
        try
        {
            using var payload = JsonDocument.Parse(row.PayloadJson);
            if (payload.RootElement.TryGetProperty("proposalId", out var rawId)
                && rawId.ValueKind == JsonValueKind.String
                && Guid.TryParse(rawId.GetString(), out var parsedId)
                && parsedId != Guid.Empty)
                proposalId = parsedId;
        }
        catch (JsonException)
        {
            return;
        }
        if (proposalId is null)
            return;

        var proposal = await db.Proposals.FirstOrDefaultAsync(item =>
            item.Id == proposalId.Value && item.ProjectId == projectId, ct);
        if (proposal is null
            || proposal.Status != ProposalStatus.Applying
            || proposal.ApplyCommandId != row.Id)
            return;

        var now = DateTime.UtcNow;
        var applied = success && IsConfirmedResult(proposal, resultJson);
        if (applied)
            proposal.CompleteApply(now);
        else
            proposal.ConflictApply(success ? "ApplyResultInvalid" : "WorkstationApplyFailed", now);

        var orgId = await db.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is { } resolvedOrgId)
        {
            var actor = new ActorContext(
                ActorType.Worker, null, false, null, null, projectId, resolvedOrgId, ApiTokenCapability.None);
            db.AuditEvents.Add(AuditEventFactory.ForActor(
                actor,
                applied ? "proposal.patch.applied" : "proposal.patch.apply_conflicted",
                "Proposal",
                proposal.Id.ToString("D"),
                resolvedOrgId,
                projectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    newState = proposal.Status.ToString(),
                    resultCode = applied ? "Verified" : success ? "InvalidResult" : "ApplyFailed"
                })));
        }
    }

    private static bool IsConfirmedResult(Proposal proposal, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)
            || !RepoPatchValidation.TryRead(proposal.PayloadJson, out _, out var expectedFiles))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("projectId", out var projectElement)
                || projectElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(projectElement.GetString(), out var resultProjectId)
                || resultProjectId != proposal.ProjectId
                || !root.TryGetProperty("proposalId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idElement.GetString(), out var resultId)
                || resultId != proposal.Id
                || !root.TryGetProperty("applied", out var appliedElement)
                || appliedElement.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("files", out var filesElement)
                || filesElement.ValueKind != JsonValueKind.Array
                || filesElement.GetArrayLength() != expectedFiles.Count)
                return false;

            foreach (var expected in expectedFiles)
            {
                var actual = filesElement.EnumerateArray().SingleOrDefault(file =>
                    file.ValueKind == JsonValueKind.Object
                    && file.TryGetProperty("projectPath", out var path)
                    && path.ValueKind == JsonValueKind.String
                    && string.Equals(path.GetString(), expected.ProjectPath, StringComparison.Ordinal));
                if (actual.ValueKind != JsonValueKind.Object)
                    return false;

                if (expected.ResultHash is null)
                {
                    if (!actual.TryGetProperty("deleted", out var deleted) || deleted.ValueKind != JsonValueKind.True)
                        return false;
                }
                else if (!actual.TryGetProperty("resultHash", out var hash)
                    || hash.ValueKind != JsonValueKind.String
                    || !string.Equals(hash.GetString(), expected.ResultHash, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class ChatSessionCompletion : ICommandCompletion
{
    public async Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        // Resolves a streaming session only on a confirmed ChatPrompt failure; never claims success.
        if (success || row.Kind != WorkstationCommandKind.ChatPrompt)
            return;
        var sessionId = DevicePayloadReader.ReadGuid(row.PayloadJson, "chatSessionId");
        if (sessionId is null)
            return;
        // Guarded: a concurrent completion or device write that already left Streaming wins.
        await db.ChatSessions.IgnoreQueryFilters()
            .Where(s => s.Id == sessionId.Value && s.Status == ChatSessionStatus.Streaming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(s => s.Status, ChatSessionStatus.Idle)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);
    }
}

internal sealed class PipelineSessionCompletion : ICommandCompletion
{
    private static string ClipFailureReason(string value) => value.Length <= 4000 ? value : value[..4000];

    public async Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.RunPipelineSession || row.ProjectId is not { } projectId)
            return;

        Guid? sessionId = DevicePayloadReader.ReadGuid(row.PayloadJson, "sessionId");
        var sessionInfo = await db.PipelineSessions.IgnoreQueryFilters()
            .Where(s => s.Id == (sessionId ?? Guid.Empty) && s.ProjectId == projectId)
            .Select(s => new { s.Role, s.SubtaskId, s.Id, s.TaskId })
            .FirstOrDefaultAsync(ct);
        string? externalSessionId = null;
        var commitSha = success ? ReadCommitSha(resultJson) : null;
        if (success && !string.IsNullOrWhiteSpace(resultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("pipelineSessionId", out var rawId)
                    && rawId.ValueKind == JsonValueKind.String
                    && Guid.TryParse(rawId.GetString(), out var returnedId)
                    && sessionId == returnedId
                    && root.TryGetProperty("externalSessionId", out var rawExternal)
                    && rawExternal.ValueKind == JsonValueKind.String)
                    externalSessionId = rawExternal.GetString();
            }
            catch (JsonException)
            {
                // Malformed results fall through to the guarded failure transition below.
            }
        }

        if (success && !string.IsNullOrWhiteSpace(externalSessionId) && sessionId is { } completedId)
        {
            var completedAt = DateTime.UtcNow;
            var closed = await db.PipelineSessions.IgnoreQueryFilters()
                .Where(s => s.Id == completedId && s.ProjectId == projectId
                    && (s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ExternalSessionId, externalSessionId)
                    .SetProperty(x => x.Status, SessionStatus.Closed)
                    .SetProperty(x => x.ClosedAt, completedAt)
                    .SetProperty(x => x.LastActivityAt, completedAt)
                    .SetProperty(x => x.FailureReason, (string?)null), ct);
            if (closed == 1 && sessionInfo?.Role == PipelineRole.Actor)
            {
                if (commitSha is not null && sessionInfo.SubtaskId is { } completedSubtaskId)
                {
                    await db.Subtasks
                        .Where(subtask => subtask.Id == completedSubtaskId
                            && subtask.TaskId == sessionInfo.TaskId
                            && subtask.ProjectId == projectId
                            && subtask.Status == SubtaskStatus.Done)
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(subtask => subtask.DiffRef, commitSha), ct);
                }

                // A clean turn that skipped subtask_report_result must not strand InProgress.
                await FailActorSubtaskAsync(db, projectId, sessionInfo.TaskId, sessionInfo.SubtaskId,
                    sessionInfo.Id, completedAt, "Actor session ended without reporting a subtask result.", ct);
            }
            return;
        }

        // A confirmed failure and a malformed success payload both mean the pipeline
        // has no usable external session to continue. Do not strand it in Active.
        if (sessionId is { } failedId)
        {
            var now = DateTime.UtcNow;
            var reason = ClipFailureReason(success
                ? "Pipeline command completed without a usable external session id."
                : error ?? "Pipeline command failed.");
            var failed = await db.PipelineSessions.IgnoreQueryFilters()
                .Where(s => s.Id == failedId && s.ProjectId == projectId && (s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, SessionStatus.Failed)
                    .SetProperty(x => x.ClosedAt, now)
                    .SetProperty(x => x.LastActivityAt, now)
                    .SetProperty(x => x.FailureReason, reason), ct);
            if (failed == 1 && sessionInfo?.Role == PipelineRole.Actor)
                await FailActorSubtaskAsync(db, projectId, sessionInfo.TaskId, sessionInfo.SubtaskId, sessionInfo.Id, now, reason, ct);
        }
    }

    private static string? ReadCommitSha(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("commitSha", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            var sha = value.GetString()?.Trim();
            return sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<int> FailActorSubtaskAsync(
        IBeaconDb db, Guid projectId, Guid taskId, Guid? subtaskId, Guid sessionId,
        DateTime now, string reason, CancellationToken ct)
    {
        if (subtaskId is not { } id)
            return 0;

        var subtask = await db.Subtasks.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == id && s.TaskId == taskId && s.ProjectId == projectId
                && (s.Status == SubtaskStatus.InProgress || s.Status == SubtaskStatus.WaitingForUser))
            .Select(s => new { s.ProfileKey, s.Status })
            .FirstOrDefaultAsync(ct);
        if (subtask is null)
            return 0;

        var failed = await db.Subtasks.IgnoreQueryFilters()
            .Where(s => s.Id == id && s.ProjectId == projectId
                && (s.Status == SubtaskStatus.InProgress || s.Status == SubtaskStatus.WaitingForUser))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, SubtaskStatus.Failed)
                .SetProperty(x => x.Summary, reason)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (failed == 0)
            return 0;

        var attempt = await db.TaskWorkspaceSections.IgnoreQueryFilters().CountAsync(section =>
            section.TaskId == taskId && section.ProjectId == projectId
            && section.AuthorSubtaskId == id && section.Kind == "Report", ct) + 1;
        db.TaskWorkspaceSections.Add(TaskWorkspaceReports.Create(
            taskId, projectId, id, attempt, reason, reason, sessionId, subtask.ProfileKey));
        return failed;
    }
}

internal sealed class EvalRunCompletion : ICommandCompletion
{
    public async Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        if (success)
            await CompleteAsync(db, row, resultJson, ct);
        else
            await FailAsync(db, row, error ?? "Command failed.", ct);
    }

    private static async Task CompleteAsync(IBeaconDb db, WorkstationCommand row, string? resultJson, CancellationToken ct)
    {
        var runId = DevicePayloadReader.ReadGuid(row.PayloadJson, "evalRunId") ?? DevicePayloadReader.ReadGuid(resultJson, "evalRunId");
        if (runId is null || string.IsNullOrWhiteSpace(resultJson))
            return;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var r = doc.RootElement;
            var promptTokens = DevicePayloadReader.ReadInt(r, "promptTokens");
            var completionTokens = DevicePayloadReader.ReadInt(r, "completionTokens");
            var turnCount = DevicePayloadReader.ReadInt(r, "turnCount");
            var interrupted = r.TryGetProperty("interrupted", out var interruptedElement)
                && interruptedElement.ValueKind == JsonValueKind.True;
            int? exitCode = interrupted ? null : DevicePayloadReader.ReadNullableInt(r, "exitCode");
            string? checkOutput = r.TryGetProperty("checkOutput", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
                ? outputElement.GetString()
                : null;
            string? transcriptRef = r.TryGetProperty("transcriptRef", out var transcriptElement) && transcriptElement.ValueKind == JsonValueKind.String
                ? transcriptElement.GetString()
                : null;
            await CompleteRunAsync(db, runId.Value, promptTokens, completionTokens, turnCount, exitCode, transcriptRef, checkOutput, ct);
        }
        catch (JsonException)
        {
        }
    }

    private static async Task FailAsync(IBeaconDb db, WorkstationCommand row, string error, CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.RunEvalTurn)
            return;
        var runId = DevicePayloadReader.ReadGuid(row.PayloadJson, "evalRunId");
        if (runId is null)
            return;
        await CompleteRunAsync(db, runId.Value, 0, 0, 0, null, null, error, ct);
    }

    private static async Task CompleteRunAsync(
        IBeaconDb db, Guid runId, int promptTokens, int completionTokens, int turnCount,
        int? exitCode, string? transcriptRef, string? checkOutput, CancellationToken ct)
    {
        var run = await db.EvalRuns.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == runId, ct);
        if (run is null || run.CompletedAt is not null)
            return;
        run.Complete(promptTokens, completionTokens, turnCount, exitCode, transcriptRef, checkOutput);
        // Guarded: only the first completion of the run is stored.
        await db.EvalRuns.IgnoreQueryFilters()
            .Where(e => e.Id == runId && e.CompletedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.PromptTokens, run.PromptTokens)
                .SetProperty(e => e.CompletionTokens, run.CompletionTokens)
                .SetProperty(e => e.TurnCount, run.TurnCount)
                .SetProperty(e => e.CheckExitCode, run.CheckExitCode)
                .SetProperty(e => e.CheckOutput, run.CheckOutput)
                .SetProperty(e => e.Passed, run.Passed)
                .SetProperty(e => e.CompletedAt, run.CompletedAt)
                .SetProperty(e => e.TranscriptRef, run.TranscriptRef), ct);
    }
}

internal sealed class ReviewCheckCompletion : ICommandCompletion
{
    public async Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.RunReviewCheck)
            return;
        var reviewRunId = DevicePayloadReader.ReadGuid(row.PayloadJson, "reviewRunId");
        if (reviewRunId is null)
            return;
        var run = await db.ReviewRuns.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reviewRunId.Value, ct);
        if (run is null || run.Status != ReviewRunStatus.Started)
            return;
        if (row.ProjectId is Guid projectId && run.ProjectId != projectId)
            return;
        if (DevicePayloadReader.ReadGuid(row.PayloadJson, "taskId") != run.TaskId
            || row.DeviceId == Guid.Empty)
            return;

        if (!success)
        {
            run.Fail(error);
        }
        else
        {
            int? exitCode = null;
            string? output = null;
            string? sourceSha = null;
            string? treeSha = null;
            string? reportedCommandHash = null;
            string? checkedBaseBranch = null;
            try
            {
                using var result = JsonDocument.Parse(resultJson ?? "{}");
                var root = result.RootElement;
                exitCode = DevicePayloadReader.ReadNullableInt(root, "exitCode");
                output = root.TryGetProperty("checkOutput", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                sourceSha = root.TryGetProperty("checkedSourceSha", out var source) && source.ValueKind == JsonValueKind.String
                    ? source.GetString()
                    : null;
                treeSha = root.TryGetProperty("checkedTreeSha", out var tree) && tree.ValueKind == JsonValueKind.String
                    ? tree.GetString()
                    : null;
                reportedCommandHash = root.TryGetProperty("checkCommandHash", out var hash) && hash.ValueKind == JsonValueKind.String
                    ? hash.GetString()
                    : null;
                checkedBaseBranch = root.TryGetProperty("checkedBaseBranch", out var branch) && branch.ValueKind == JsonValueKind.String
                    ? branch.GetString()
                    : null;
            }
            catch (JsonException)
            {
                exitCode = null;
            }

            var trustedCommand = DevicePayloadReader.ReadString(row.PayloadJson, "checkCommand");
            var actualCommandHash = string.IsNullOrWhiteSpace(trustedCommand)
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trustedCommand))).ToLowerInvariant();
            var commandMatches = actualCommandHash is not null
                && string.Equals(actualCommandHash, run.CheckCommandHash, StringComparison.OrdinalIgnoreCase)
                && (reportedCommandHash is null
                    || string.Equals(actualCommandHash, reportedCommandHash, StringComparison.OrdinalIgnoreCase));
            var hasAnyGitEvidence = sourceSha is not null || treeSha is not null
                || reportedCommandHash is not null || checkedBaseBranch is not null;
            var hasCompleteGitEvidence = IsGitSha(sourceSha)
                && IsGitSha(treeSha)
                && string.Equals(actualCommandHash, reportedCommandHash, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(checkedBaseBranch)
                && checkedBaseBranch.Length <= 200
                && !checkedBaseBranch.Contains("..", StringComparison.Ordinal)
                && !checkedBaseBranch.Contains('\\');

            if (exitCode == 0 && commandMatches && hasCompleteGitEvidence)
                run.CompleteCheck(output, $"cmd:{row.Id:D}", sourceSha!, treeSha!, row.DeviceId, checkedBaseBranch!);
            else if (exitCode == 0 && commandMatches && !hasAnyGitEvidence)
                // Legacy Snapshot-mode review remains a valid review result, but cannot be
                // used as a landing proof because it is not bound to a task-branch commit.
                run.Complete(output, $"cmd:{row.Id:D}");
            else
                run.Fail(exitCode == 0
                    ? "Review check result did not prove the pinned command and checked Git revision."
                    : string.IsNullOrWhiteSpace(output) ? "Check did not pass." : output);
        }

        // Guarded: only the first completion of the started run is stored.
        await db.ReviewRuns.IgnoreQueryFilters()
            .Where(r => r.Id == run.Id && r.Status == ReviewRunStatus.Started)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Findings, run.Findings)
                .SetProperty(r => r.ArtifactRef, run.ArtifactRef)
                .SetProperty(r => r.TranscriptRef, run.TranscriptRef)
                .SetProperty(r => r.Status, run.Status)
                .SetProperty(r => r.CompletedAt, run.CompletedAt)
                .SetProperty(r => r.CheckCommandHash, run.CheckCommandHash)
                .SetProperty(r => r.CheckedSourceSha, run.CheckedSourceSha)
                .SetProperty(r => r.CheckedTreeSha, run.CheckedTreeSha)
                .SetProperty(r => r.CheckedDeviceId, run.CheckedDeviceId)
                .SetProperty(r => r.CheckedBaseBranch, run.CheckedBaseBranch), ct);
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}

internal sealed class GitLandCompletion : ICommandCompletion
{
    public async Task ApplyAsync(
        IBeaconDb db,
        WorkstationCommand row,
        bool success,
        string? resultJson,
        string? error,
        CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.GitLand || row.ProjectId is not { } projectId)
            return;
        var attemptId = DevicePayloadReader.ReadGuid(row.PayloadJson, "landingAttemptId");
        if (attemptId is null)
            return;

        var attempt = await db.LandingAttempts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == attemptId.Value && item.ProjectId == projectId, ct);
        if (attempt is null)
            return;

        var payloadTaskId = DevicePayloadReader.ReadGuid(row.PayloadJson, "taskId");
        var payloadAction = DevicePayloadReader.ReadString(row.PayloadJson, "action");
        var payloadTargetBranch = DevicePayloadReader.ReadString(row.PayloadJson, "targetBranch");
        var payloadReviewBaseBranch = DevicePayloadReader.ReadString(row.PayloadJson, "reviewBaseBranch");
        var payloadAllowIntegrationFallback = ReadBooleanPayload(row.PayloadJson, "allowIntegrationFallback");
        var pinnedPolicy = ProjectGitPolicyCatalog.Defaults(attempt.GitPolicyRevision);
        var pinnedReviewBaseBranch = attempt.TargetBranch;
        if (!string.Equals(attempt.GitPolicySnapshotJson, "{}", StringComparison.Ordinal)
            && (!ProjectGitPolicyCatalog.TryRead(
                    attempt.GitPolicySnapshotJson, attempt.GitPolicyRevision, out pinnedPolicy)
                || !ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                    attempt.GitPolicySnapshotJson, attempt.TargetBranch, out pinnedReviewBaseBranch)))
            return;
        var expectedReviewBaseBranch = payloadReviewBaseBranch ?? attempt.TargetBranch;
        var expectedIntegrationFallback = payloadAction == "Preview"
            && pinnedPolicy.AutonomyLevel != 1
            && pinnedPolicy.DirtyCheckoutBehavior == "LandViaIntegration";
        var payloadSourceSha = DevicePayloadReader.ReadString(row.PayloadJson, "sourceSha");
        var payloadReviewTreeSha = DevicePayloadReader.ReadString(row.PayloadJson, "reviewTreeSha");
        var payloadCommandHash = DevicePayloadReader.ReadString(row.PayloadJson, "checkCommandHash");
        var payloadTargetHeadSha = DevicePayloadReader.ReadString(row.PayloadJson, "targetHeadSha");
        var payloadTargetTreeSha = DevicePayloadReader.ReadString(row.PayloadJson, "targetTreeSha");
        var payloadMergeCommitSha = DevicePayloadReader.ReadString(row.PayloadJson, "mergeCommitSha");
        var expectedAction = attempt.Status switch
        {
            LandingAttemptStatus.PreviewPending => "Preview",
            LandingAttemptStatus.ExecutePending => "Execute",
            LandingAttemptStatus.RevertPending => "Revert",
            _ => null
        };
        var actionBindingValid = payloadAction switch
        {
            "Preview" => payloadTargetHeadSha is null && payloadTargetTreeSha is null && payloadMergeCommitSha is null,
            "Execute" => string.Equals(payloadTargetHeadSha, attempt.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                && string.Equals(payloadTargetTreeSha, attempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                && payloadMergeCommitSha is null,
            "Revert" => string.Equals(payloadMergeCommitSha, attempt.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
                && payloadTargetHeadSha is null && payloadTargetTreeSha is null,
            _ => false
        };
        if (row.DeviceId != attempt.DeviceId
            || payloadTaskId != attempt.TaskId
            || !string.Equals(payloadSourceSha, attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payloadReviewTreeSha, attempt.ReviewTreeSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payloadCommandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payloadTargetBranch, attempt.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(expectedReviewBaseBranch, pinnedReviewBaseBranch, StringComparison.Ordinal)
            || payloadAllowIntegrationFallback != expectedIntegrationFallback
            || !string.Equals(payloadAction, expectedAction, StringComparison.Ordinal)
            || !actionBindingValid)
            return;

        var previousState = attempt.Status;
        string resultCode;
        string action;
        if (!success)
        {
            if (!IsInFlight(attempt.Status))
                return;
            if (attempt.Status is LandingAttemptStatus.ExecutePending or LandingAttemptStatus.RevertPending)
            {
                // A lost acknowledgement after a compare-and-swap may mean the Git ref
                // already moved. Keep the state retryable; GitLand's attempt marker makes
                // the next command reconcile the exact commit instead of merging twice.
                attempt.MarkCommandCompletionUnknown();
                resultCode = "CommandCompletionUnknown";
            }
            else
            {
                attempt.Fail("GitLandCommandFailed");
                resultCode = "GitLandCommandFailed";
            }
            action = ActionFor(previousState);
        }
        else
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(resultJson ?? "{}");
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("GitLand result must be a JSON object.");
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                if (attempt.Status is LandingAttemptStatus.ExecutePending or LandingAttemptStatus.RevertPending)
                    attempt.MarkCommandCompletionUnknown();
                else if (IsInFlight(attempt.Status))
                    attempt.Fail("InvalidGitLandResult");
                resultCode = attempt.FailureCode ?? "InvalidGitLandResult";
                action = ActionFor(previousState);
                goto Audit;
            }

            var status = ReadString(root, "status");
            switch (status)
            {
                case "previewed":
                    {
                        var targetBranch = ReadString(root, "targetBranch");
                        var targetHead = ReadString(root, "targetHeadSha");
                        var tree = ReadString(root, "candidateTreeSha");
                        var checkHash = ReadString(root, "checkOutputHash");
                        var exitCode = DevicePayloadReader.ReadNullableInt(root, "checkExitCode");
                        if (attempt.Status == LandingAttemptStatus.PreviewPending
                            && IsAllowedPreviewTarget(targetBranch, attempt.TargetBranch, pinnedPolicy)
                            && IsGitSha(targetHead)
                            && IsGitSha(tree)
                            && IsSha256(checkHash)
                            && exitCode == 0)
                        {
                            attempt.RecordPreview(targetBranch!, targetHead!, tree, checkHash, exitCode, null);
                            resultCode = "Previewed";
                            action = "pipeline.landing.preview_completed";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "UnexpectedOrInvalidPreviewResult");
                            resultCode = "InvalidPreviewEvidence";
                            action = "pipeline.landing.preview_failed";
                        }
                        break;
                    }
                case "conflicted":
                    {
                        var targetBranch = ReadString(root, "targetBranch");
                        var targetHead = ReadString(root, "targetHeadSha");
                        var conflicts = root.TryGetProperty("conflicts", out var files)
                            && files.ValueKind == JsonValueKind.Array
                                ? files.EnumerateArray()
                                    .Where(item => item.ValueKind == JsonValueKind.String)
                                    .Select(item => item.GetString())
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Take(201)
                                    .ToArray()
                                : [];
                        if (attempt.Status == LandingAttemptStatus.PreviewPending
                            && IsAllowedPreviewTarget(targetBranch, attempt.TargetBranch, pinnedPolicy)
                            && IsGitSha(targetHead)
                            && conflicts.Length is > 0 and <= 200)
                        {
                            attempt.RecordPreview(
                                targetBranch!, targetHead!, null, null, null,
                                JsonSerializer.Serialize(conflicts));
                            resultCode = "Conflicted";
                            action = "pipeline.landing.conflicted";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "UnexpectedOrInvalidConflictResult");
                            resultCode = "InvalidConflictEvidence";
                            action = "pipeline.landing.preview_failed";
                        }
                        break;
                    }
                case "failed":
                    {
                        var failureCode = ReadString(root, "errorCode");
                        FailIfInFlight(attempt, IsSafeFailureCode(failureCode) ? failureCode! : "GitLandOperationFailed");
                        resultCode = IsSafeFailureCode(failureCode) ? failureCode! : "GitLandOperationFailed";
                        action = ActionFor(previousState);
                        break;
                    }
                case "execute_unknown":
                    {
                        var mergeCommit = ReadString(root, "mergeCommitSha");
                        var errorCode = ReadString(root, "errorCode");
                        var identityMatches = attempt.Status == LandingAttemptStatus.ExecutePending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "sourceSha"), attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "targetHeadSha"), attempt.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "candidateTreeSha"), attempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                            && (mergeCommit is null || IsGitSha(mergeCommit));
                        if (attempt.Status == LandingAttemptStatus.ExecutePending)
                        {
                            // Keep the attempt exclusive until a retry can reconcile the durable
                            // merge marker. Even malformed evidence must not release the unique
                            // active-attempt slot while a target ref may already have moved.
                            attempt.MarkCommandCompletionUnknown();
                            resultCode = identityMatches
                                ? IsSafeFailureCode(errorCode) ? errorCode! : "CommandCompletionUnknown"
                                : "InvalidExecuteUnknownEvidence";
                        }
                        else
                        {
                            resultCode = "UnexpectedExecuteUnknownState";
                        }
                        action = "pipeline.landing.execute_failed";
                        break;
                    }
                case "merge_queued":
                    {
                        var mergeRequestId = ReadString(root, "mergeRequestId");
                        var mergeRequestStatus = ReadString(root, "mergeRequestStatus");
                        var requestIdValid = string.IsNullOrWhiteSpace(mergeRequestId)
                            ? string.Equals(mergeRequestStatus, "enqueued", StringComparison.Ordinal)
                            : Guid.TryParse(mergeRequestId, out var parsedMergeRequestId)
                                && parsedMergeRequestId != Guid.Empty;
                        var identityMatches = attempt.Status == LandingAttemptStatus.ExecutePending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "sourceSha"), attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "targetHeadSha"), attempt.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "candidateTreeSha"), attempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                            && requestIdValid
                            && mergeRequestStatus is "pending" or "enqueued";
                        if (identityMatches)
                        {
                            attempt.MarkGitHubMergeQueued(mergeRequestId, mergeRequestStatus);
                            resultCode = "GitHubMergeQueued";
                            action = "pipeline.landing.execute_queued";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "InvalidGitHubMergeQueueEvidence");
                            resultCode = "InvalidGitHubMergeQueueEvidence";
                            action = "pipeline.landing.execute_failed";
                        }
                        break;
                    }
                case "landed":
                    {
                        var mergeCommit = ReadString(root, "mergeCommitSha");
                        var tree = ReadString(root, "candidateTreeSha");
                        var checkExitCode = DevicePayloadReader.ReadNullableInt(root, "postMergeCheckExitCode");
                        var checkOutputHash = ReadString(root, "postMergeCheckOutputHash");
                        if (attempt.Status == LandingAttemptStatus.ExecutePending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "sourceSha"), attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "targetHeadSha"), attempt.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(tree, attempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                            && IsGitSha(mergeCommit) && IsGitSha(tree))
                        {
                            if (string.Equals(attempt.CandidateTreeSha, tree, StringComparison.OrdinalIgnoreCase)
                                && checkExitCode == 0 && IsSha256(checkOutputHash))
                            {
                                attempt.RecordLanded(mergeCommit!, tree!, checkExitCode.Value, checkOutputHash!);
                                resultCode = "Landed";
                                action = "pipeline.landing.completed";
                            }
                            else
                            {
                                attempt.RecordLandedUnverified(
                                    mergeCommit!,
                                    "PostMergeEvidenceInvalid",
                                    checkExitCode,
                                    checkOutputHash);
                                resultCode = "PostMergeEvidenceInvalid";
                                action = "pipeline.landing.execute_failed";
                            }
                        }
                        else
                        {
                            FailIfInFlight(attempt, "InvalidLandedEvidence");
                            resultCode = "InvalidLandedEvidence";
                            action = "pipeline.landing.execute_failed";
                        }
                        break;
                    }
                case "landed_unverified":
                case "landed_tree_mismatch":
                case "landed_metadata_mismatch":
                    {
                        var mergeCommit = ReadString(root, "mergeCommitSha");
                        var errorCode = ReadString(root, "errorCode");
                        var checkExitCode = DevicePayloadReader.ReadNullableInt(root, "postMergeCheckExitCode");
                        var checkOutputHash = ReadString(root, "postMergeCheckOutputHash");
                        if (attempt.Status == LandingAttemptStatus.ExecutePending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "sourceSha"), attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "targetHeadSha"), attempt.TargetHeadSha, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(root, "candidateTreeSha"), attempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                            && IsGitSha(mergeCommit))
                        {
                            attempt.RecordLandedUnverified(
                                mergeCommit!,
                                IsSafeFailureCode(errorCode) ? errorCode! : "PostMergeVerificationFailed",
                                checkExitCode,
                                checkOutputHash);
                            resultCode = IsSafeFailureCode(errorCode) ? errorCode! : "PostMergeVerificationFailed";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "InvalidLandedUnverifiedEvidence");
                            resultCode = "InvalidLandedUnverifiedEvidence";
                        }
                        action = "pipeline.landing.execute_failed";
                        break;
                    }
                case "reverted":
                    {
                        var revertCommit = ReadString(root, "revertCommitSha");
                        if (attempt.Status == LandingAttemptStatus.RevertPending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "mergeCommitSha"), attempt.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
                            && IsGitSha(revertCommit))
                        {
                            attempt.RecordReverted(revertCommit!);
                            resultCode = "Reverted";
                            action = "pipeline.landing.reverted";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "InvalidRevertEvidence");
                            resultCode = "InvalidRevertEvidence";
                            action = "pipeline.landing.revert_failed";
                        }
                        break;
                    }
                case "reverted_unverified":
                    {
                        var revertCommit = ReadString(root, "revertCommitSha");
                        var failureCode = ReadString(root, "errorCode");
                        if (attempt.Status == LandingAttemptStatus.RevertPending
                            && string.Equals(ReadString(root, "targetBranch"), attempt.TargetBranch, StringComparison.Ordinal)
                            && string.Equals(ReadString(root, "mergeCommitSha"), attempt.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
                            && IsGitSha(revertCommit))
                        {
                            attempt.RecordRevertedUnverified(
                                revertCommit!,
                                IsSafeFailureCode(failureCode) ? failureCode! : "TargetWorktreeUpdateFailed");
                            resultCode = IsSafeFailureCode(failureCode) ? failureCode! : "TargetWorktreeUpdateFailed";
                        }
                        else
                        {
                            FailIfInFlight(attempt, "InvalidRevertUnverifiedEvidence");
                            resultCode = "InvalidRevertUnverifiedEvidence";
                        }
                        action = "pipeline.landing.revert_failed";
                        break;
                    }
                default:
                    {
                        FailIfInFlight(attempt, "UnknownGitLandResult");
                        resultCode = "UnknownGitLandResult";
                        action = "pipeline.landing.command_failed";
                        break;
                    }
            }
        }

    Audit:
        var orgId = await db.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (attempt.Status == LandingAttemptStatus.Landed && orgId is { } forwardPortOrgId)
            await EnsureForwardPortTaskAsync(db, attempt, projectId, forwardPortOrgId, ct);
        if (orgId is { } resolvedOrgId)
        {
            var actor = new ActorContext(
                ActorType.Worker, null, false, null, null,
                projectId, resolvedOrgId, ApiTokenCapability.None);
            var outcome = attempt.Status is LandingAttemptStatus.Conflicted
                or LandingAttemptStatus.Failed
                or LandingAttemptStatus.RevertFailed
                or LandingAttemptStatus.RevertedUnverified
                or LandingAttemptStatus.LandedUnverified
                || string.Equals(attempt.FailureCode, "CommandCompletionUnknown", StringComparison.Ordinal)
                    ? AuditOutcome.Failed
                    : AuditOutcome.Success;
            var details = new Dictionary<string, object>
            {
                ["previousState"] = previousState.ToString(),
                ["newState"] = attempt.Status.ToString(),
                ["resultCode"] = resultCode,
                ["sourceBranch"] = attempt.SourceBranch,
                ["targetBranch"] = attempt.TargetBranch,
                ["sourceSha"] = attempt.SourceSha,
                ["reviewTreeSha"] = attempt.ReviewTreeSha,
                ["reviewCommandHash"] = attempt.ReviewCommandHash,
                ["reviewRunId"] = attempt.ReviewRunId.ToString("D"),
                ["deviceId"] = attempt.DeviceId.ToString("D")
            };
            if (attempt.TargetHeadSha is not null)
                details["targetHeadSha"] = attempt.TargetHeadSha;
            if (attempt.CandidateTreeSha is not null)
                details["candidateTreeSha"] = attempt.CandidateTreeSha;
            if (attempt.CandidateCheckOutputHash is not null)
                details["candidateCheckOutputHash"] = attempt.CandidateCheckOutputHash;
            if (attempt.PostMergeCheckOutputHash is not null)
                details["postMergeCheckOutputHash"] = attempt.PostMergeCheckOutputHash;
            if (attempt.MergeCommitSha is not null)
                details["mergeCommitSha"] = attempt.MergeCommitSha;
            if (attempt.RevertCommitSha is not null)
                details["revertCommitSha"] = attempt.RevertCommitSha;

            db.AuditEvents.Add(AuditEventFactory.ForActor(
                actor,
                action,
                "LandingAttempt",
                attempt.Id.ToString("D"),
                resolvedOrgId,
                projectId,
                outcome,
                JsonSerializer.Serialize(details)));
        }
    }

    private static async Task EnsureForwardPortTaskAsync(
        IBeaconDb db,
        LandingAttempt attempt,
        Guid projectId,
        Guid orgId,
        CancellationToken ct)
    {
        var hotfixTask = await db.Tasks.FirstOrDefaultAsync(
            task => task.Id == attempt.TaskId
                && task.ProjectId == projectId
                && task.HotfixSourceReleaseId != null,
            ct);
        if (hotfixTask is null)
            return;

        var alreadyCreated = await db.Tasks.AnyAsync(
            task => task.ProjectId == projectId
                && task.ForwardPortOfTaskId == hotfixTask.Id,
            ct);
        if (alreadyCreated)
            return;

        var title = "Forward-port Hotfix: " + hotfixTask.Title;
        if (title.Length > 500)
            title = title[..500];
        var description =
            $"Automatically created after Hotfix task {hotfixTask.Id:D} was successfully landed.\n\n" +
            $"Source Release: {hotfixTask.HotfixSourceReleaseId:D}\n" +
            $"Production deployed SHA: {hotfixTask.HotfixDeployedSha}\n" +
            $"Hotfix target branch: {hotfixTask.HotfixTargetBranch}\n" +
            $"Hotfix landing merge commit: {attempt.MergeCommitSha}\n\n" +
            "Forward-port the fix into the project's normal development/review base branch. " +
            "This is an ordinary Beacon task: use the standard Planner, Actor, Review and landing workflow. " +
            "Do not create a release tag or change the official project version.";
        var forwardPort = TaskItem.Create(
            title, projectId, TaskPriority.High, TaskType.Task, description);
        forwardPort.SetForwardPortOfTask(hotfixTask.Id);
        if (hotfixTask.PipelineDeviceId is { } pinnedDeviceId)
            forwardPort.PinPipelineDevice(pinnedDeviceId);

        db.Tasks.Add(forwardPort);
        new ContentWriter().AddVersion(
            db, forwardPort, "System", "ProjectBeacon.GitLandCompletion");
        var actor = new ActorContext(
            ActorType.Worker, null, false, null, null,
            projectId, orgId, ApiTokenCapability.None);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            actor,
            "task.forward_port.created",
            "Task",
            forwardPort.Id.ToString("D"),
            orgId,
            projectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                releaseId = hotfixTask.HotfixSourceReleaseId!.Value.ToString("D"),
                sourceSha = hotfixTask.HotfixDeployedSha!,
                targetBranch = attempt.TargetBranch,
                mergeCommitSha = attempt.MergeCommitSha!,
                deployedSha = hotfixTask.HotfixDeployedSha!,
                hotfixRequestId = hotfixTask.HotfixRequestId!.Value.ToString("D")
            })));
    }

    private static bool IsAllowedPreviewTarget(
        string? resultTargetBranch,
        string expectedTargetBranch,
        ProjectGitPolicyDto policy) =>
        string.Equals(resultTargetBranch, expectedTargetBranch, StringComparison.Ordinal)
        || (string.Equals(resultTargetBranch, ProjectGitPolicyCatalog.IntegrationBranchName, StringComparison.Ordinal)
            && !string.Equals(expectedTargetBranch, ProjectGitPolicyCatalog.IntegrationBranchName, StringComparison.Ordinal)
            && policy.DirtyCheckoutBehavior == "LandViaIntegration");

    private static bool ReadBooleanPayload(string? json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "{}");
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsInFlight(LandingAttemptStatus status) =>
        status is LandingAttemptStatus.PreviewPending
            or LandingAttemptStatus.Previewed
            or LandingAttemptStatus.ExecutePending
            or LandingAttemptStatus.RevertPending;

    private static void FailIfInFlight(LandingAttempt attempt, string code)
    {
        if (IsInFlight(attempt.Status))
            attempt.Fail(code);
    }

    private static string ActionFor(LandingAttemptStatus status) =>
        status switch
        {
            LandingAttemptStatus.PreviewPending => "pipeline.landing.preview_failed",
            LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending => "pipeline.landing.execute_failed",
            LandingAttemptStatus.RevertPending => "pipeline.landing.revert_failed",
            _ => "pipeline.landing.command_failed"
        };

    private static bool IsSafeFailureCode(string? value) =>
        value is { Length: > 0 and <= 80 }
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}


internal sealed class ReleasePrepareCompletion : ICommandCompletion
{
    public async Task ApplyAsync(
        IBeaconDb db,
        WorkstationCommand row,
        bool success,
        string? resultJson,
        string? error,
        CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.ReleasePrepare || row.ProjectId is not { } projectId)
            return;
        if (!TryObject(row.PayloadJson, out var payload))
            return;

        var action = ReadString(payload, "action");
        if (action is not ("Prepare" or "VerifyLanding"))
            return;
        var releaseId = ReadGuid(payload, "releaseId");
        var candidateId = ReadGuid(payload, "candidateId");
        if (releaseId == Guid.Empty || candidateId == Guid.Empty)
            return;

        var release = await db.Releases.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == releaseId && item.ProjectId == projectId, ct);
        if (release is null || release.PreparationDeviceId != row.DeviceId
            || release.CandidateId != candidateId)
            return;

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return;

        if (action == "Prepare")
        {
            if (release.PreparationCommandId != row.Id
                || release.PreparationStatus != ReleasePreparationStatus.PrepareQueued)
                return;

            if (!success)
            {
                release.RecordPrepareFailure(row.Id, error ?? row.Error ?? "ReleasePrepare workstation command failed.");
                AddAudit(db, project, releaseId, "project.release.prepare_failed",
                    new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
                return;
            }

            if (!TryValidatePrepareResult(payload, resultJson, out var invalidReason))
            {
                release.RecordPrepareFailure(row.Id, invalidReason ?? "ReleasePrepare returned invalid evidence.");
                AddAudit(db, project, releaseId, "project.release.prepare_failed",
                    new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
                return;
            }

            release.RecordPrepareSuccess(row.Id, resultJson!);
            if (TryObject(resultJson, out var preparedResult))
            {
                AddAudit(db, project, releaseId, "project.release.prepared",
                    new
                    {
                        candidateId,
                        workstationId = row.DeviceId,
                        preparedCommitSha = ReadString(preparedResult, "preparedCommitSha"),
                        pullRequestUrl = ReadString(preparedResult, "pullRequestUrl"),
                        preparationRevision = release.PreparationRevision
                    });
            }
            return;
        }

        if (release.VerificationCommandId != row.Id
            || release.PreparationStatus != ReleasePreparationStatus.VerifyQueued)
            return;

        if (!success)
        {
            release.RecordVerificationFailure(row.Id, error ?? row.Error ?? "VerifyLanding workstation command failed.");
            AddAudit(db, project, releaseId, "project.release.verify_failed",
                new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
            return;
        }

        if (!TryValidateVerifyResult(payload, release.PreparationResultJson, resultJson,
            release, row.DeviceId, out var verified, out var invalidVerifyReason))
        {
            release.RecordVerificationFailure(row.Id, invalidVerifyReason ?? "VerifyLanding returned invalid evidence.");
            AddAudit(db, project, releaseId, "project.release.verify_failed",
                new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
            return;
        }

        if (project.VersionPolicyRevision != verified.VersionPolicyRevision
            || release.Revision != verified.ReleaseRevision
            || !ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var currentPolicy, out _)
            || !ProjectVersionPolicyCatalog.TryNormalizeVersion(currentPolicy, verified.Version, out _, out _))
        {
            release.RecordVerificationFailure(row.Id,
                "Project version policy or Release changed after Prepare. Candidate evidence was not committed.");
            AddAudit(db, project, releaseId, "project.release.verify_failed",
                new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
            return;
        }

        var currentScope = await ReleasePreparePayload.BuildSnapshotAsync(
            db, release, project, currentPolicy, ct);
        if (currentScope.Error is not null
            || !ReleasePreparePayload.MatchesSnapshot(verified.SnapshotJson, currentScope.Json))
        {
            release.RecordVerificationFailure(row.Id,
                "The Release milestone/task scope changed after Prepare. Candidate evidence was not committed; cancel this Release and prepare a new one from the updated task scope.");
            AddAudit(db, project, releaseId, "project.release.verify_failed",
                new
                {
                    candidateId,
                    workstationId = row.DeviceId,
                    error = release.PreparationError,
                    snapshotBuildError = currentScope.Error
                });
            return;
        }

        try
        {
            var snapshot = ReleaseCandidateSnapshot.Create(
                projectId,
                releaseId,
                candidateId,
                row.DeviceId,
                verified.Version,
                verified.TagName,
                verified.TargetBranch,
                verified.BaseSha,
                verified.PreparedCommitSha,
                verified.CandidateSha,
                verified.CandidateTreeSha,
                verified.VersionPolicyRevision,
                verified.ReleaseRevision,
                verified.ManifestSha256,
                verified.SnapshotJson,
                verified.VerifiedAtUtc);
            release.MarkCandidate(snapshot);
            project.SetOfficialVersionFromRelease(verified.Version);
            db.ReleaseCandidateSnapshots.Add(snapshot);
            AddAudit(db, project, releaseId, "project.release.candidate_created",
                new
                {
                    candidateId,
                    workstationId = row.DeviceId,
                    version = verified.Version,
                    tagName = verified.TagName,
                    targetBranch = verified.TargetBranch,
                    candidateSha = verified.CandidateSha,
                    candidateTreeSha = verified.CandidateTreeSha,
                    manifestSha256 = verified.ManifestSha256,
                    officialVersion = project.OfficialVersion
                });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            release.RecordVerificationFailure(row.Id, "Candidate evidence was rejected: " + Clip(exception.Message));
            AddAudit(db, project, releaseId, "project.release.verify_failed",
                new { candidateId, workstationId = row.DeviceId, error = release.PreparationError });
        }
    }

    private sealed record VerifiedEvidence(
        string Version,
        string TagName,
        string TargetBranch,
        string BaseSha,
        string PreparedCommitSha,
        string CandidateSha,
        string CandidateTreeSha,
        int VersionPolicyRevision,
        int ReleaseRevision,
        string ManifestSha256,
        string SnapshotJson,
        DateTime VerifiedAtUtc);

    private static bool TryValidatePrepareResult(
        JsonObject payload, string? resultJson, out string? error)
    {
        error = null;
        if (!TryObject(resultJson, out var result))
            return Fail("Prepare result is not a JSON object.", out error);

        var projectId = ReadGuid(payload, "projectId");
        var releaseId = ReadGuid(payload, "releaseId");
        var candidateId = ReadGuid(payload, "candidateId");
        var version = ReadString(payload, "version");
        var targetBranch = ReadString(payload, "targetBranch");
        var snapshotJson = ReadString(payload, "snapshotJson");
        if (ReadString(result, "action") != "Prepare"
            || ReadGuid(result, "projectId") != projectId
            || ReadGuid(result, "releaseId") != releaseId
            || ReadGuid(result, "candidateId") != candidateId
            || ReadString(result, "version") != version
            || ReadString(result, "targetBranch") != targetBranch
            || ReadString(result, "snapshotJson") != snapshotJson)
            return Fail("Prepare result identity or immutable snapshot does not match the queued payload.", out error);

        var tagName = ReadString(result, "tagName");
        var baseSha = ReadString(result, "baseSha");
        var preparedSha = ReadString(result, "preparedCommitSha");
        var manifestSha = ReadString(result, "manifestSha256");
        var releaseBranch = ReadString(result, "releaseBranch");
        var pullRequestUrl = ReadString(result, "pullRequestUrl");
        var pullRequestNumber = ReadInt(result, "pullRequestNumber");
        var publicationTagCreated = ReadBool(result, "publicationTagCreated");
        if (!ProjectVersionPolicyCatalog.TryRead(ReadString(payload, "versionPolicyJson"), out var policy, out _)
            || tagName != ProjectVersionPolicyCatalog.FormatTag(policy, version)
            || !IsGitSha(baseSha)
            || !IsGitSha(preparedSha)
            || !IsSha256(manifestSha)
            || !ProjectVersionPolicyCatalog.IsSafeBranchName(releaseBranch)
            || pullRequestNumber < 1
            || !IsGitHubPullRequestUrl(pullRequestUrl)
            || ReadPullRequestNumber(pullRequestUrl) != pullRequestNumber
            || publicationTagCreated)
            return Fail("Prepare result is missing valid commit, manifest, tag, PR or no-tag evidence.", out error);

        return true;
    }

    private static bool TryValidateVerifyResult(
        JsonObject payload,
        string? prepareResultJson,
        string? verifyResultJson,
        Release release,
        Guid deviceId,
        out VerifiedEvidence evidence,
        out string? error)
    {
        evidence = null!;
        error = null;
        if (!TryObject(prepareResultJson, out var prepared)
            || !TryObject(verifyResultJson, out var verified))
            return Fail("Prepare or VerifyLanding result is not a JSON object.", out error);

        var projectId = ReadGuid(payload, "projectId");
        var releaseId = ReadGuid(payload, "releaseId");
        var candidateId = ReadGuid(payload, "candidateId");
        var version = ReadString(payload, "version");
        var tagName = ReadString(prepared, "tagName");
        var targetBranch = ReadString(payload, "targetBranch");
        var preparedSha = ReadString(prepared, "preparedCommitSha");
        var manifestSha = ReadString(prepared, "manifestSha256");
        var baseSha = ReadString(prepared, "baseSha");
        var snapshotJson = ReadString(payload, "snapshotJson");
        var versionPolicyRevision = ReadInt(payload, "versionPolicyRevision");
        var releaseRevision = ReadInt(payload, "releaseRevision");

        if (release.PreparationDeviceId != deviceId
            || release.CandidateId != candidateId
            || ReadString(payload, "action") != "VerifyLanding"
            || ReadString(verified, "action") != "VerifyLanding"
            || ReadGuid(verified, "projectId") != projectId
            || ReadGuid(verified, "releaseId") != releaseId
            || ReadGuid(verified, "candidateId") != candidateId
            || ReadString(verified, "version") != version
            || ReadString(verified, "tagName") != tagName
            || ReadString(verified, "targetBranch") != targetBranch
            || ReadString(verified, "baseSha") != baseSha
            || ReadString(verified, "preparedCommitSha") != preparedSha
            || ReadString(verified, "manifestSha256") != manifestSha
            || ReadString(verified, "snapshotJson") != snapshotJson
            || ReadInt(verified, "versionPolicyRevision") != versionPolicyRevision
            || ReadInt(verified, "releaseRevision") != releaseRevision
            || versionPolicyRevision < 1
            || releaseRevision != release.Revision)
            return Fail("VerifyLanding result does not match the exact pinned Prepare evidence.", out error);

        var candidateSha = ReadString(verified, "candidateSha");
        var candidateTreeSha = ReadString(verified, "candidateTreeSha");
        if (!IsGitSha(baseSha) || !IsGitSha(preparedSha)
            || !IsGitSha(candidateSha) || !IsGitSha(candidateTreeSha)
            || !IsSha256(manifestSha)
            || !DateTime.TryParse(ReadString(verified, "verifiedAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var verifiedAt))
            return Fail("VerifyLanding result contains invalid Git hashes, manifest hash or verification time.", out error);

        evidence = new VerifiedEvidence(
            version, tagName, targetBranch, baseSha, preparedSha, candidateSha, candidateTreeSha,
            versionPolicyRevision, releaseRevision, manifestSha, snapshotJson,
            DateTime.SpecifyKind(verifiedAt, DateTimeKind.Utc));
        return true;
    }

    private static void AddAudit(IBeaconDb db, Project project, Guid releaseId, string action, object details)
    {
        var actor = new ActorContext(
            ActorType.Worker, null, false, null, null, project.Id, project.OrgId, ApiTokenCapability.None);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            actor, action, "Release", releaseId.ToString("D"), project.OrgId, project.Id,
            detailsJson: JsonSerializer.Serialize(details)));
    }

    /// <summary>
    /// A workstation may lose its acknowledgement after pushing the immutable tag.
    /// Accept a negative tag-pushed assertion only when the failure result is bound to
    /// this exact server-queued Publish payload and includes an explicit boolean.
    /// Incomplete or mismatched evidence must never make a Candidate cancellable.
    /// </summary>
    internal static bool ReadTagPushedEvidence(JsonObject payload, string? resultJson)
    {
        if (!TryObject(resultJson, out var result)
            || !IsPublicationResultForPayload(payload, result)
            || !TryReadBool(result, "tagPushed", out var tagPushed))
            return true;
        return tagPushed;
    }

    private static bool IsPublicationResultForPayload(JsonObject payload, JsonObject result) =>
        ReadString(result, "action") == "Publish"
        && ReadGuid(result, "projectId") != Guid.Empty
        && ReadGuid(result, "projectId") == ReadGuid(payload, "projectId")
        && ReadGuid(result, "releaseId") != Guid.Empty
        && ReadGuid(result, "releaseId") == ReadGuid(payload, "releaseId")
        && ReadGuid(result, "candidateId") != Guid.Empty
        && ReadGuid(result, "candidateId") == ReadGuid(payload, "candidateId")
        && !string.IsNullOrWhiteSpace(ReadString(result, "version"))
        && string.Equals(ReadString(result, "version"), ReadString(payload, "version"), StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ReadString(result, "tagName"))
        && string.Equals(ReadString(result, "tagName"), ReadString(payload, "tagName"), StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ReadString(result, "candidateSha"))
        && string.Equals(ReadString(result, "candidateSha"), ReadString(payload, "candidateSha"), StringComparison.OrdinalIgnoreCase);

    private static bool TryObject(string? json, out JsonObject value)
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

    private static string ReadString(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<string>(out var text) ? text ?? string.Empty : string.Empty;

    private static Guid ReadGuid(JsonObject value, string name) =>
        Guid.TryParse(ReadString(value, name), out var id) ? id : Guid.Empty;

    private static int ReadInt(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<int>(out var number) ? number : 0;

    private static bool TryReadBool(JsonObject value, string name, out bool flag)
    {
        flag = false;
        return value[name] is JsonValue element && element.TryGetValue<bool>(out flag);
    }

    private static bool ReadBool(JsonObject value, string name) =>
        TryReadBool(value, name, out var flag) && flag;

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsGitHubPullRequestUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && System.Text.RegularExpressions.Regex.IsMatch(
            uri.AbsolutePath, @"^/[^/]+/[^/]+/pull/[1-9][0-9]*$", RegexOptions.CultureInvariant);

    private static int ReadPullRequestNumber(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return 0;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 && segments[2] == "pull"
            && int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string Clip(string value) => value.Length <= 4000 ? value : value[..4000];
}

internal sealed class ReleasePublishCompletion : ICommandCompletion
{
    public async Task ApplyAsync(
        IBeaconDb db,
        WorkstationCommand row,
        bool success,
        string? resultJson,
        string? error,
        CancellationToken ct)
    {
        if (row.Kind != WorkstationCommandKind.PublishRelease || row.ProjectId is not { } projectId)
            return;
        if (!TryObject(row.PayloadJson, out var payload))
            return;

        var releaseId = ReadGuid(payload, "releaseId");
        var candidateId = ReadGuid(payload, "candidateId");
        if (releaseId == Guid.Empty || candidateId == Guid.Empty)
            return;

        var release = await db.Releases.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == releaseId && item.ProjectId == projectId, ct);
        if (release is null
            || release.PublicationStatus != ReleasePublicationStatus.Queued
            || release.PublicationCommandId != row.Id
            || release.PublicationDeviceId != row.DeviceId
            || release.PreparationDeviceId != row.DeviceId
            || release.CandidateId != candidateId)
            return;

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return;

        var candidate = await db.ReleaseCandidateSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.ProjectId == projectId
                && item.ReleaseId == releaseId && item.CandidateId == candidateId, ct);
        if (candidate is null)
        {
            release.RecordPublicationFailure(row.Id,
                "Immutable Candidate snapshot was not found while processing publication completion.",
                tagPushed: true);
            AddAudit(db, project, releaseId, "project.release.publish_failed",
                new { candidateId, error = release.PublicationError, evidenceMissing = true });
            return;
        }

        var hasResult = TryObject(resultJson, out var result);
        var trustedResult = hasResult && IsPublicationResultForPayload(payload, result);
        var tagPushed = trustedResult && TryReadBool(result, "tagPushed", out var reportedTagPushed)
            ? reportedTagPushed
            : true; // Missing or mismatched evidence is conservative: the immutable tag may already exist.
        var runId = trustedResult ? ReadLong(result, "workflowRunId") : null;
        var runUrl = trustedResult ? ReadString(result, "workflowRunUrl") : null;

        if (!success)
        {
            var reportedError = trustedResult ? ReadString(result, "error") : null;
            release.RecordPublicationFailure(row.Id,
                reportedError ?? error ?? row.Error ?? "Release publication workstation command failed.",
                tagPushed, runId, runUrl);
            AddAudit(db, project, releaseId, "project.release.publish_failed",
                new
                {
                    candidateId,
                    tagPushed = release.PublicationTagPushed,
                    workflowRunId = release.DeploymentWorkflowRunId,
                    workflowRunUrl = release.DeploymentWorkflowRunUrl,
                    error = release.PublicationError
                });
            return;
        }

        if (!TryValidateResult(payload, resultJson, release, candidate, row.DeviceId,
            out var proof, out var invalidReason))
        {
            release.RecordPublicationFailure(row.Id,
                invalidReason ?? "Workstation returned invalid deployment evidence.",
                tagPushed: true, runId, runUrl);
            AddAudit(db, project, releaseId, "project.release.publish_failed",
                new
                {
                    candidateId,
                    tagPushed = true,
                    workflowRunId = release.DeploymentWorkflowRunId,
                    workflowRunUrl = release.DeploymentWorkflowRunUrl,
                    error = release.PublicationError
                });
            return;
        }

        try
        {
            release.CompletePublication(
                row.Id,
                proof.TagName,
                proof.WorkflowRunId,
                proof.WorkflowRunUrl,
                proof.DeployedVersion,
                proof.DeployedCommitSha,
                proof.DeployedAtUtc,
                resultJson!);
            AddAudit(db, project, releaseId, "project.release.published",
                new
                {
                    candidateId,
                    version = proof.DeployedVersion,
                    tagName = proof.TagName,
                    candidateSha = candidate.CandidateSha,
                    deployedCommitSha = proof.DeployedCommitSha,
                    workflowRunId = proof.WorkflowRunId,
                    workflowRunUrl = proof.WorkflowRunUrl,
                    deployedAtUtc = proof.DeployedAtUtc
                });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            release.RecordPublicationFailure(row.Id,
                "Deployment evidence was rejected: " + Clip(exception.Message),
                tagPushed: true, proof.WorkflowRunId, proof.WorkflowRunUrl);
            AddAudit(db, project, releaseId, "project.release.publish_failed",
                new
                {
                    candidateId,
                    tagPushed = true,
                    workflowRunId = proof.WorkflowRunId,
                    workflowRunUrl = proof.WorkflowRunUrl,
                    error = release.PublicationError
                });
        }
    }

    private sealed record PublicationProof(
        string TagName,
        long WorkflowRunId,
        string WorkflowRunUrl,
        string DeployedVersion,
        string DeployedCommitSha,
        DateTime DeployedAtUtc);

    private static bool TryValidateResult(
        JsonObject payload,
        string? resultJson,
        Release release,
        ReleaseCandidateSnapshot candidate,
        Guid deviceId,
        out PublicationProof proof,
        out string? error)
    {
        proof = null!;
        error = null;
        if (!TryObject(resultJson, out var result))
            return Fail("PublishRelease result is not a JSON object.", out error);

        var tagName = ReadString(result, "tagName");
        var version = ReadString(result, "version");
        var candidateSha = ReadString(result, "candidateSha");
        var deployedVersion = ReadString(result, "deployedVersion");
        var deployedSha = ReadString(result, "deployedCommitSha");
        var workflowRunId = ReadLong(result, "workflowRunId");
        var workflowRunUrl = ReadString(result, "workflowRunUrl");
        var deployedAtRaw = ReadString(result, "deployedAtUtc");
        var workflowStatus = ReadString(result, "workflowStatus");
        var workflowConclusion = ReadString(result, "workflowConclusion");
        var deploymentStatus = ReadString(result, "deploymentStatus");

        if (ReadString(result, "action") != "Publish"
            || ReadGuid(result, "projectId") != ReadGuid(payload, "projectId")
            || ReadGuid(result, "releaseId") != ReadGuid(payload, "releaseId")
            || ReadGuid(result, "candidateId") != ReadGuid(payload, "candidateId")
            || !string.Equals(tagName, ReadString(payload, "tagName"), StringComparison.Ordinal)
            || !string.Equals(tagName, candidate.TagName, StringComparison.Ordinal)
            || !string.Equals(version, ReadString(payload, "version"), StringComparison.Ordinal)
            || !string.Equals(version, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(candidateSha, ReadString(payload, "candidateSha"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidateSha, candidate.CandidateSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(deployedVersion, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(deployedSha, candidate.CandidateSha, StringComparison.OrdinalIgnoreCase)
            || workflowRunId is null or <= 0
            || !IsGitHubActionsRunUrl(workflowRunUrl)
            || !string.Equals(workflowStatus, "completed", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(workflowConclusion, "success", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(deploymentStatus, "in_sync", StringComparison.Ordinal)
            || !ReadBool(result, "tagPushed")
            || release.PublicationDeviceId != deviceId
            || candidate.PreparedByDeviceId != deviceId
            || !DateTime.TryParse(deployedAtRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deployedAt))
            return Fail("PublishRelease result does not prove the exact immutable Candidate tag, successful workflow and production version/gitSha.", out error);

        proof = new PublicationProof(
            tagName, workflowRunId.Value, workflowRunUrl, deployedVersion, deployedSha,
            DateTime.SpecifyKind(deployedAt, DateTimeKind.Utc));
        return true;
    }

    private static void AddAudit(IBeaconDb db, Project project, Guid releaseId, string action, object details)
    {
        var actor = new ActorContext(
            ActorType.Worker, null, false, null, null, project.Id, project.OrgId, ApiTokenCapability.None);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            actor, action, "Release", releaseId.ToString("D"), project.OrgId, project.Id,
            detailsJson: JsonSerializer.Serialize(details)));
    }

    private static bool TryObject(string? json, out JsonObject value)
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

    private static string ReadString(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<string>(out var text) ? text ?? string.Empty : string.Empty;

    private static Guid ReadGuid(JsonObject value, string name) =>
        Guid.TryParse(ReadString(value, name), out var id) ? id : Guid.Empty;

    private static long? ReadLong(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<long>(out var number) ? number : null;

    private static bool ReadBool(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<bool>(out var flag) && flag;

    private static bool IsGitHubActionsRunUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(uri.AbsolutePath, @"/[^/]+/[^/]+/actions/runs/[1-9][0-9]*$", RegexOptions.CultureInvariant);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string Clip(string value) => value.Length <= 4000 ? value : value[..4000];
}

internal sealed class DesiredStateCompletion : ICommandCompletion
{
    public async Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        if (!success)
            return;
        var revision = DesiredState.ReadRevision(row.PayloadJson) ?? DesiredState.ReadRevision(resultJson);
        if (revision is not { } value)
            return;
        if (row.Kind is not (WorkstationCommandKind.SaveWorkstation or WorkstationCommandKind.ReconcileDesired))
            return;
        await db.DaemonDevices
            .Where(d => d.Id == row.DeviceId && d.AppliedRevision < value && d.DesiredRevision >= value)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AppliedRevision, value), ct);
    }
}
