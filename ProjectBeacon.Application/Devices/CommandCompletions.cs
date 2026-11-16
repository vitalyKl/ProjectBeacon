namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Domain.Entities.Content;
using Domain.Entities.Audit;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Application.Proposals;
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
                            && string.Equals(targetBranch, attempt.TargetBranch, StringComparison.Ordinal)
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
                            && string.Equals(targetBranch, attempt.TargetBranch, StringComparison.Ordinal)
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
