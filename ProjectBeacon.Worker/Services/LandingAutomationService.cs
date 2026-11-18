namespace ProjectBeacon.Worker.Services;

using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Projects;
using Application.Tasks;
using Domain.Enums;
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
            var expiredDispatchLease = now.AddMinutes(-2);
            var candidates = await db.LandingAttempts
                .AsNoTracking()
                .Where(attempt =>
                    (attempt.Status == LandingAttemptStatus.RevertPending
                        && attempt.EscalationCode == null
                        && (attempt.FailureCode == null
                            || attempt.FailureCode == "CommandCompletionUnknown"
                            || (attempt.FailureCode == "RevertEnqueuePending"
                                && attempt.UpdatedAt < expiredDispatchLease)))
                    || (attempt.Status == LandingAttemptStatus.ExecutePending
                        && attempt.EscalationCode == null
                        && (attempt.FailureCode == null
                            || attempt.FailureCode == "CommandCompletionUnknown"
                            || (attempt.FailureCode == "ExecuteEnqueuePending"
                                && attempt.UpdatedAt < expiredDispatchLease)))
                    || (attempt.EscalationCode == null
                        && (attempt.Status == LandingAttemptStatus.Previewed
                            || attempt.Status == LandingAttemptStatus.LandedUnverified)))
                // A command already in the durable queue owns this attempt. Exclude it before
                // applying Take(200), otherwise the same oldest active Revert commands can
                // occupy every worker batch and starve unrelated landings indefinitely.
                .Where(attempt => !db.WorkstationCommands.Any(command =>
                    command.ProjectId == attempt.ProjectId
                    && command.Kind == WorkstationCommandKind.GitLand
                    && (command.Status == WorkstationCommandStatus.Pending
                        || command.Status == WorkstationCommandStatus.Running)
                    && command.PayloadJson.Contains(attempt.Id.ToString())))
                .OrderBy(attempt => attempt.UpdatedAt)
                .ThenBy(attempt => attempt.Id)
                .Take(200)
                .ToListAsync(ct);

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
                        if (candidate.Status == LandingAttemptStatus.ExecutePending)
                            await FailAndEscalateAsync(db, candidate.Id, LandingAttemptStatus.ExecutePending,
                                "GitPolicyChangedSincePreview", ct);
                        else
                            await SetEscalationAsync(db, candidate.Id, LandingAttemptStatus.Previewed,
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

                    if (policy.AutonomyLevel == 1)
                    {
                        if (candidate.Status == LandingAttemptStatus.ExecutePending)
                            await FailAndEscalateAsync(db, candidate.Id, LandingAttemptStatus.ExecutePending,
                                "IntegrationBranchNotAvailable", ct);
                        else
                            await SetEscalationAsync(db, candidate.Id, LandingAttemptStatus.Previewed,
                                "IntegrationBranchNotAvailable", ct);
                        continue;
                    }

                    if (policy.AutonomyLevel == 2)
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
                            _logger.LogWarning(
                                "Automatic revert for landing {LandingAttemptId} was not queued: {Error}",
                                candidate.Id, result.Error);
                    }
                }
            }

            if (dispatched > 0)
                _logger.LogInformation("Dispatched {Count} policy-driven landing commands", dispatched);
            return dispatched;
        }
    }

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

    private static async Task SetEscalationAsync(
        BeaconDbContext db,
        Guid attemptId,
        LandingAttemptStatus expectedStatus,
        string escalationCode,
        CancellationToken ct)
    {
        var attempt = await db.LandingAttempts
            .FirstOrDefaultAsync(item => item.Id == attemptId && item.Status == expectedStatus, ct);
        if (attempt is null || string.Equals(attempt.EscalationCode, escalationCode, StringComparison.Ordinal))
            return;
        attempt.SetEscalationCode(escalationCode);
        await db.SaveChangesAsync(ct);
    }
}
