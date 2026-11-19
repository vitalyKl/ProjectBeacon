namespace ProjectBeacon.Worker.Services;

using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Projects;
using Application.Tasks;
using Domain.Enums;
using Domain.Entities.Tasks;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable policy dispatcher for landing attempts. It re-discovers work from database state
/// every tick, so process restarts do not lose automatic Execute or post-check revert actions.
/// Actual command creation remains guarded by LandingCommandDispatch inside the handlers.
/// </summary>
public sealed class LandingAutomationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LandingAutomationService> _logger;
    private readonly TimeProvider _timeProvider;

    public LandingAutomationService(
        IServiceScopeFactory scopeFactory,
        ILogger<LandingAutomationService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _timeProvider);
        do
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Landing automation tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> DispatchAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconDbContext>>();
        var appDbFactory = scope.ServiceProvider.GetRequiredService<IBeaconDbFactory>();
        var enqueue = scope.ServiceProvider.GetRequiredService<EnqueueCommandHandler>();

        await using var db = dbFactory.CreateDbContext();
        using (TenantScope.EnterUnscoped())
        {
            await TenantRlsSession.ApplyAsync(db, null, null, unscoped: true);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var candidates = await GetDispatchCandidatesAsync(db, now, ct);

            var dispatched = 0;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!ProjectGitPolicyCatalog.TryRead(
                        candidate.GitPolicySnapshotJson, candidate.GitPolicyRevision, out var policy))
                {
                    await SetEscalationAsync(db, candidate.Id, candidate.Status, "InvalidGitPolicySnapshot", ct);
                    continue;
                }

                var project = await db.Projects.IgnoreQueryFilters()
                    .Where(item => item.Id == candidate.ProjectId)
                    .Select(item => new { item.OrgId, item.GitPolicyRevision })
                    .FirstOrDefaultAsync(ct);
                if (project is null)
                {
                    await SetEscalationAsync(db, candidate.Id, candidate.Status, "ProjectUnavailable", ct);
                    continue;
                }

                var actor = new ActorContext(
                    ActorType.Worker, null, false, null, null,
                    candidate.ProjectId, project.OrgId, ApiTokenCapability.None);

                if (candidate.Status is LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending)
                {
                    if (project.GitPolicyRevision != candidate.GitPolicyRevision)
                    {
                        await FailAndEscalateAsync(db, candidate.Id, candidate.Status,
                            "GitPolicyChangedSincePreview", ct);
                        continue;
                    }

                    if (policy.AutonomyLevel == 0)
                    {
                        await SetEscalationAsync(db, candidate.Id, candidate.Status,
                            candidate.Status == LandingAttemptStatus.ExecutePending
                                ? "ManualExecuteRetryRequired"
                                : "ManualExecutionRequired", ct);
                        continue;
                    }

                    if (ShouldDispatchExecute(candidate.Status, policy.AutonomyLevel))
                    {
                        var result = await new ExecuteLandingHandler(appDbFactory, enqueue).HandleAsync(
                            new ExecuteLandingCommand(
                                new ExecuteLandingRequest(candidate.TaskId, candidate.Id, Automatic: true),
                                actor),
                            ct);
                        if (result.Success)
                            dispatched++;
                        else
                            _logger.LogWarning(
                                "Automatic execute recovery for landing {LandingAttemptId} was not queued: {Error}",
                                candidate.Id, result.Error);
                    }
                    continue;
                }

                if (candidate.Status is LandingAttemptStatus.LandedUnverified or LandingAttemptStatus.RevertPending)
                {
                    if (candidate.Status == LandingAttemptStatus.LandedUnverified
                        && candidate.PostMergeCheckExitCode is not > 0)
                    {
                        await SetEscalationAsync(db, candidate.Id, candidate.Status,
                            "PostMergeVerificationNeedsReview", ct);
                        continue;
                    }

                    // RevertPending means a human or a prior policy decision already requested
                    // the revert. AutoRevertMode controls only whether a new revert is initiated
                    // after a failed post-merge check; it must never strand an existing revert
                    // when its workstation command was lost or its dispatch lease expired.
                    if (candidate.Status == LandingAttemptStatus.LandedUnverified
                        && policy.AutoRevertMode == "Off")
                    {
                        await SetEscalationAsync(db, candidate.Id, candidate.Status, "PostMergeCheckFailed", ct);
                        continue;
                    }

                    if (ShouldDispatchRevert(
                        candidate.Status, policy.AutoRevertMode, candidate.PostMergeCheckExitCode))
                    {
                        var result = await new RevertLandingHandler(appDbFactory, enqueue).HandleAsync(
                            new RevertLandingCommand(
                                new RevertLandingRequest(candidate.TaskId, candidate.Id, Automatic: true),
                                actor),
                            ct);
                        if (result.Success)
                            dispatched++;
                        else
                        {
                            // A missing/offline workstation must not occupy the first 200
                            // candidates every 15 seconds. Persist a short retry backoff while
                            // keeping the attempt visible and manually retryable.
                            await SetRevertDispatchUnavailableAsync(db, candidate.Id, ct);
                            _logger.LogWarning(
                                "Automatic revert for landing {LandingAttemptId} was not queued: {Error}",
                                candidate.Id, result.Error);
                        }
                    }
                }
            }

            if (dispatched > 0)
                _logger.LogInformation("Dispatched {Count} policy-driven landing commands", dispatched);
            return dispatched;
        }
    }

    internal static async Task<List<LandingAttempt>> GetDispatchCandidatesAsync(
        BeaconDbContext db,
        DateTime now,
        CancellationToken ct = default)
    {
        // Read active GitLand payloads and parse the attempt identity as data. Guid.ToString()
        // inside a correlated EF predicate is provider-dependent (and SQLite does not map it
        // to the same representation as JSON serialization). Resolve exact IDs once, then let
        // both PostgreSQL and SQLite execute a normal Guid NOT IN predicate.
        var activePayloads = await db.WorkstationCommands
            .AsNoTracking()
            .Where(command => command.Kind == WorkstationCommandKind.GitLand
                && (command.Status == WorkstationCommandStatus.Pending
                    || command.Status == WorkstationCommandStatus.Running))
            .Select(command => command.PayloadJson)
            .ToListAsync(ct);

        var activeAttemptIds = new HashSet<Guid>();
        foreach (var payload in activePayloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("landingAttemptId", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out var attemptId)
                    && attemptId != Guid.Empty)
                    activeAttemptIds.Add(attemptId);
            }
            catch (JsonException)
            {
                // Malformed commands cannot bind an attempt; completion handling will fail them.
            }
        }

        var expiredDispatchLease = now.AddMinutes(-2);
        var query = db.LandingAttempts
            .AsNoTracking()
            .Where(attempt =>
                (attempt.Status == LandingAttemptStatus.RevertPending
                    && ((attempt.EscalationCode == null
                            && (attempt.FailureCode == null
                                || attempt.FailureCode == "CommandCompletionUnknown"
                                || (attempt.FailureCode == "RevertEnqueuePending"
                                    && attempt.UpdatedAt < expiredDispatchLease)))
                        || (attempt.EscalationCode == "RevertDispatchUnavailable"
                            && attempt.UpdatedAt < expiredDispatchLease)))
                || (attempt.Status == LandingAttemptStatus.ExecutePending
                    && attempt.EscalationCode == null
                    && (attempt.FailureCode == null
                        || attempt.FailureCode == "CommandCompletionUnknown"
                        || (attempt.FailureCode == "ExecuteEnqueuePending"
                            && attempt.UpdatedAt < expiredDispatchLease)))
                || (attempt.Status == LandingAttemptStatus.Previewed
                    && attempt.EscalationCode == null)
                || (attempt.Status == LandingAttemptStatus.LandedUnverified
                    && (attempt.EscalationCode == null
                        || (attempt.EscalationCode == "RevertDispatchUnavailable"
                            && attempt.UpdatedAt < expiredDispatchLease))));

        if (activeAttemptIds.Count > 0)
            query = query.Where(attempt => !activeAttemptIds.Contains(attempt.Id));

        return await query
            .OrderBy(attempt => attempt.UpdatedAt)
            .ThenBy(attempt => attempt.Id)
            .Take(200)
            .ToListAsync(ct);
    }

    internal static bool ShouldDispatchExecute(LandingAttemptStatus status, int autonomyLevel) =>
        autonomyLevel is 1 or 2
        && status is (LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending);

    internal static bool ShouldDispatchRevert(
        LandingAttemptStatus status,
        string autoRevertMode,
        int? postMergeCheckExitCode)
    {
        if (status == LandingAttemptStatus.RevertPending)
            return true;

        return status == LandingAttemptStatus.LandedUnverified
            && string.Equals(autoRevertMode, "OnPostCheckFail", StringComparison.Ordinal)
            && postMergeCheckExitCode is > 0;
    }

    private static async Task FailAndEscalateAsync(
        BeaconDbContext db,
        Guid attemptId,
        LandingAttemptStatus expectedStatus,
        string escalationCode,
        CancellationToken ct)
    {
        var attempt = await db.LandingAttempts
            .FirstOrDefaultAsync(item => item.Id == attemptId && item.Status == expectedStatus, ct);
        if (attempt is null)
            return;
        attempt.Fail(escalationCode);
        attempt.SetEscalationCode(escalationCode);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetRevertDispatchUnavailableAsync(
        BeaconDbContext db,
        Guid attemptId,
        CancellationToken ct)
    {
        var attempt = await db.LandingAttempts
            .FirstOrDefaultAsync(item => item.Id == attemptId
                && (item.Status == LandingAttemptStatus.LandedUnverified
                    || item.Status == LandingAttemptStatus.RevertPending), ct);
        if (attempt is null)
            return;
        attempt.SetEscalationCode("RevertDispatchUnavailable");
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetEscalationAsync(
        BeaconDbContext db,
        Guid attemptId,
        LandingAttemptStatus expectedStatus,
        string escalationCode,
        CancellationToken ct)
    {
        var attempt = await db.LandingAttempts
            .FirstOrDefaultAsync(item => item.Id == attemptId && item.Status == expectedStatus, ct);
        if (attempt is null)
            return;
        // Refresh UpdatedAt even for the same escalation code so retryable failures
        // observe the backoff window instead of re-entering every worker tick.
        attempt.SetEscalationCode(escalationCode);
        await db.SaveChangesAsync(ct);
    }
}
