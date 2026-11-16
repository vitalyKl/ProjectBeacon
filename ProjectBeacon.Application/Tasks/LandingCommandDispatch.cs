namespace ProjectBeacon.Application.Tasks;

using System.Text.Json;
using Application.Persistence;
using Domain.Entities.Devices;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Makes the gap between persisting a LandingAttempt and creating its workstation command
/// recoverable. A two-minute dispatch lease prevents concurrent HTTP retries from duplicating
/// commands; after a crashed API process leaves no command row, a later retry can reclaim it.
/// </summary>
internal static class LandingCommandDispatch
{
    private static readonly TimeSpan DispatchLease = TimeSpan.FromMinutes(2);

    public static string MarkerFor(string action) => action switch
    {
        "Preview" => "PreviewEnqueuePending",
        "Execute" => "ExecuteEnqueuePending",
        "Revert" => "RevertEnqueuePending",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public static bool HasFreshLease(LandingAttempt attempt, string action)
    {
        var marker = MarkerFor(action);
        return string.Equals(attempt.FailureCode, marker, StringComparison.Ordinal)
            && attempt.UpdatedAt >= DateTime.UtcNow - DispatchLease;
    }

    public static async Task<WorkstationCommand?> FindActiveCommandAsync(
        IBeaconDb db,
        Guid projectId,
        Guid attemptId,
        string action,
        CancellationToken ct)
    {
        var needle = attemptId.ToString("D");
        var candidates = await db.WorkstationCommands
            .Where(command => command.ProjectId == projectId
                && command.Kind == WorkstationCommandKind.GitLand
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running)
                && command.PayloadJson.Contains(needle))
            .OrderByDescending(command => command.CreatedAt)
            .ThenByDescending(command => command.Id)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("landingAttemptId", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out var parsedId)
                    && parsedId == attemptId
                    && root.TryGetProperty("action", out var actionElement)
                    && actionElement.ValueKind == JsonValueKind.String
                    && string.Equals(actionElement.GetString(), action, StringComparison.Ordinal))
                    return candidate;
            }
            catch (JsonException)
            {
                // A malformed unrelated command must not be mistaken for this attempt.
            }
        }

        return null;
    }

    /// <summary>
    /// Claims one dispatch attempt using UpdatedAt and FailureCode as an optimistic lease.
    /// The status transition is included in the same guarded UPDATE as the lease claim.
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        IBeaconDb db,
        LandingAttempt attempt,
        string action,
        CancellationToken ct)
    {
        var marker = MarkerFor(action);
        if (HasFreshLease(attempt, action))
            return false;

        var targetStatus = action switch
        {
            "Preview" when attempt.Status == LandingAttemptStatus.PreviewPending
                => LandingAttemptStatus.PreviewPending,
            "Execute" when attempt.Status is LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending
                => LandingAttemptStatus.ExecutePending,
            "Revert" when attempt.Status is LandingAttemptStatus.Landed
                or LandingAttemptStatus.LandedUnverified
                or LandingAttemptStatus.RevertPending
                => LandingAttemptStatus.RevertPending,
            _ => (LandingAttemptStatus?)null
        };
        if (targetStatus is null)
            return false;

        var previousCode = attempt.FailureCode;
        if (previousCode is not null
            && !string.Equals(previousCode, "CommandCompletionUnknown", StringComparison.Ordinal)
            && !string.Equals(previousCode, marker, StringComparison.Ordinal))
            return false;

        var previousUpdatedAt = attempt.UpdatedAt;
        var now = DateTime.UtcNow;
        var query = db.LandingAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.Status == attempt.Status
                && candidate.UpdatedAt == previousUpdatedAt);
        query = previousCode is null
            ? query.Where(candidate => candidate.FailureCode == null)
            : query.Where(candidate => candidate.FailureCode == previousCode);

        var updated = await query.ExecuteUpdateAsync(update => update
            .SetProperty(candidate => candidate.Status, targetStatus.Value)
            .SetProperty(candidate => candidate.FailureCode, marker)
            .SetProperty(candidate => candidate.UpdatedAt, now), ct);
        return updated == 1;
    }

    public static Task<int> ClearLeaseAsync(
        IBeaconDb db,
        LandingAttempt attempt,
        string action,
        CancellationToken ct)
    {
        var marker = MarkerFor(action);
        return db.LandingAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.FailureCode == marker
                && candidate.Status == attempt.Status)
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.FailureCode, (string?)null)
                .SetProperty(candidate => candidate.UpdatedAt, DateTime.UtcNow), ct);
    }
}
