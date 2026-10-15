namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

internal interface ICommandCompletion
{
    Task ApplyAsync(IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct);
}

internal static class CommandCompletions
{
    private static readonly ICommandCompletion[] Effects =
    [
        new EvalRunCompletion(),
        new ReviewCheckCompletion(),
        new DesiredStateCompletion(),
        new ChatSessionCompletion()
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
            run.Fail(error);
        else
        {
            int? exitCode = null;
            string? output = null;
            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(resultJson);
                    exitCode = DevicePayloadReader.ReadNullableInt(doc.RootElement, "exitCode");
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

        // Guarded: only the first completion of the started run is stored.
        await db.ReviewRuns.IgnoreQueryFilters()
            .Where(r => r.Id == run.Id && r.Status == ReviewRunStatus.Started)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Findings, run.Findings)
                .SetProperty(r => r.ArtifactRef, run.ArtifactRef)
                .SetProperty(r => r.TranscriptRef, run.TranscriptRef)
                .SetProperty(r => r.Status, run.Status)
                .SetProperty(r => r.CompletedAt, run.CompletedAt), ct);
    }
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
}
