namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Domain.Entities.Devices;
using Domain.Enums;
using Infrastructure.Data;
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
        new DesiredStateCompletion()
    ];

    public static async Task ApplyAsync(
        IBeaconDb db, WorkstationCommand row, bool success, string? resultJson, string? error, CancellationToken ct)
    {
        foreach (var effect in Effects)
            await effect.ApplyAsync(db, row, success, resultJson, error, ct);
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
            var run = await db.EvalRuns.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == runId.Value, ct);
            if (run is null || run.CompletedAt is not null)
                return;
            run.Complete(promptTokens, completionTokens, turnCount, exitCode, transcriptRef, checkOutput);
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
        var run = await db.EvalRuns.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == runId.Value, ct);
        if (run is null || run.CompletedAt is not null)
            return;
        run.Complete(0, 0, 0, null, null, error);
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
        var run = await db.ReviewRuns.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == reviewRunId.Value, ct);
        if (run is null || run.Status != ReviewRunStatus.Started)
            return;
        if (row.ProjectId is Guid projectId && run.ProjectId != projectId)
            return;

        if (!success)
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
