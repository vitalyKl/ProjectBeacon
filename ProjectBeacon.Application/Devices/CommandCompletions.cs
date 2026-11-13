namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Domain.Entities.Content;
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
                && string.Equals(actualCommandHash, reportedCommandHash, StringComparison.OrdinalIgnoreCase);
            if (exitCode == 0 && commandMatches && IsGitSha(sourceSha) && IsGitSha(treeSha))
                run.CompleteCheck(output, $"cmd:{row.Id:D}", sourceSha!, treeSha!);
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
                .SetProperty(r => r.CheckedTreeSha, run.CheckedTreeSha), ct);
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
